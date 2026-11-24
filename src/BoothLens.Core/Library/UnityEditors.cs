using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace BoothLens.Core.Library;

/// <summary>Only live, recent bridge registrations are import targets; no saved path fallback.</summary>
public sealed class UnityEditors
{
    public static string DefaultRegistry => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoothLens", "Editors");
    private readonly string registry;
    private readonly Func<int,long,bool> isAlive;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public UnityEditors(string? registry = null, Func<int,long,bool>? isAlive = null)
    { this.registry = registry ?? DefaultRegistry; this.isAlive = isAlive ?? IsUnity; }
    public sealed record OpenProject(string Id, string ProjectPath, string Name, int ProcessId, bool BridgeReady, bool IsActive, string LastSeen, bool Busy,
        string? BlockReason = null, long? PendingJobId = null, bool CanResetPending = false, string? BridgeVersion = null)
    {
        // Recovery must carry a live registry validation, not trust a caller-supplied path or derived Busy flag.
        internal Func<OpenProject, long>? RecoveryValidation { get; init; }
    }
    public sealed class Heartbeat
    {
        public string? Id { get; set; }
        public string? ProjectPath { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartTicks { get; set; }
        public string? UpdatedUtc { get; set; }
        public bool IsActive { get; set; }
        public bool Busy { get; set; }
        public string? BridgeVersion { get; set; }
    }
    private Heartbeat ReadHeartbeat(string file)
    {
        ResourcePaths.RequireDirectory(ResourcePaths.Full(registry));
        ResourcePaths.NoLinks(file);
        var h = JsonSerializer.Deserialize<Heartbeat>(ReadLimited(file, 16384), Json);
        if (h == null || string.IsNullOrWhiteSpace(h.Id) || Path.GetFileNameWithoutExtension(file) != h.Id ||
            h.ProcessStartTicks <= 0 || h.ProcessStartTicks > DateTime.MaxValue.Ticks ||
            h.Id != $"{h.ProcessId}-{h.ProcessStartTicks}" || !DateTimeOffset.TryParse(h.UpdatedUtc, out var seen) ||
            DateTimeOffset.UtcNow - seen > TimeSpan.FromSeconds(30) || seen - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(5) ||
            !isAlive(h.ProcessId, h.ProcessStartTicks) || string.IsNullOrWhiteSpace(h.ProjectPath))
            throw new ArgumentException("Unity 工程心跳已过期或进程已退出，请刷新工程列表。");
        h.ProjectPath = ResourcePaths.Full(h.ProjectPath);
        ResourcePaths.RequireDirectory(h.ProjectPath);
        ResourcePaths.RequireDirectory(Path.Combine(h.ProjectPath, "Assets"));
        ResourcePaths.NoLinks(Path.Combine(h.ProjectPath, "ProjectSettings", "ProjectVersion.txt"));
        return h;
    }
    internal static byte[] ReadLimited(string file, int limit)
    {
        ResourcePaths.NoLinks(file);
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > limit) throw new IOException("BoothLens JSON 超过读取大小上限。");
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = input.Read(buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)output.Length))) > 0)
        {
            output.Write(buffer, 0, count);
            if (output.Length > limit) throw new IOException("BoothLens JSON 超过读取大小上限。");
        }
        return output.ToArray();
    }
    internal static long? JobId(string file) => long.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        && id > 0 && Path.GetFileNameWithoutExtension(file) == id.ToString(CultureInfo.InvariantCulture) ? id : null;
    private static (string? Reason, long? JobId) Pending(string project)
    {
        foreach (var folder in new[] { "running", "queue" })
        {
            var directory = Path.Combine(project, "Library", "BoothLens", folder);
            if (!Directory.Exists(directory)) continue;
            ResourcePaths.RequireDirectory(directory);
            var first = Directory.EnumerateFiles(directory, "*.json").FirstOrDefault();
            if (first == null) continue;
            ResourcePaths.NoLinks(first);
            var id = JobId(first);
            return (folder == "running" ? "检测到待处理或遗留的导入记录；请先结束当前导入，跨进程旧任务可手动清理。" : "有导入任务正在排队等待 Unity 处理。", id);
        }
        return (null, null);
    }
    private static bool CanRecover(Heartbeat h, string? reason)
    {
        if (h.Busy || reason == null) return false;
        try
        {
            var running = Path.Combine(h.ProjectPath!, "Library", "BoothLens", "running");
            if (!Directory.Exists(running)) return false;
            ResourcePaths.RequireDirectory(running);
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(running, "*.json"))
            {
                if (++count > 20000) return false;
                ResourcePaths.NoLinks(file);
                if (File.GetLastWriteTimeUtc(file).Ticks >= h.ProcessStartTicks) return false;
            }
            return count > 0;
        }
        catch (Exception ex) when (ResourcePaths.IsPathError(ex)) { return false; }
    }
    public IReadOnlyList<OpenProject> List()
    {
        var result = new List<OpenProject>();
        if (!Directory.Exists(registry)) return result;
        ResourcePaths.RequireDirectory(ResourcePaths.Full(registry));
        foreach (var file in Directory.EnumerateFiles(registry, "*.json").Take(128))
        {
            try
            {
                var h = ReadHeartbeat(file);
                string? reason; long? jobId;
                try { (reason, jobId) = Pending(h.ProjectPath!); }
                catch (Exception ex) when (ResourcePaths.IsPathError(ex)) { reason = "待处理任务目录异常，请检查 BoothLens 桥接记录。"; jobId = null; }
                result.Add(new(h.Id!, h.ProjectPath!, Path.GetFileName(h.ProjectPath!), h.ProcessId, true, h.IsActive,
                    DateTimeOffset.Parse(h.UpdatedUtc!).ToString("O"), h.Busy || reason != null,
                    h.Busy ? "Unity 正在编译、刷新、运行或处理导入，请稍后重试。" : reason, jobId, CanRecover(h, reason), h.BridgeVersion));
            }
            catch (Exception ex) when (ResourcePaths.IsPathError(ex) || ex is JsonException or InvalidOperationException) { }
        }
        return result.DistinctBy(p => p.ProjectPath, ResourcePaths.Comparer).OrderByDescending(p => p.IsActive).ThenBy(p => p.Name).ToList();
    }
    private OpenProject Select(string? id)
    {
        var open = List();
        if (open.Count == 0) throw new ArgumentException("未发现已打开且桥接在线的 Unity 工程。请在目标工程安装新版 BoothRelay.cs，等待编译和心跳后刷新。");
        return string.IsNullOrWhiteSpace(id)
            ? open.Count == 1 ? open[0] : throw new ArgumentException("同时打开了多个 Unity 工程，请在导入窗口选择目标工程。")
            : open.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException("所选 Unity 工程已关闭或桥接已离线，请刷新工程列表。");
    }
    public OpenProject Resolve(string? id)
    {
        var project = Select(id);
        if (project.Busy) throw new ArgumentException(project.BlockReason ?? "所选 Unity 编辑器忙碌，请稍后重试。");
        return project;
    }
    public OpenProject ResolveForRecovery(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("请选择要清理待处理记录的 Unity 工程。");
        var project = Select(id);
        long Validate(OpenProject candidate)
        {
            if (candidate.Id != project.Id || candidate.ProcessId != project.ProcessId ||
                !ResourcePaths.Comparer.Equals(candidate.ProjectPath, project.ProjectPath))
                throw new ArgumentException("恢复目标与已核验的 Unity 会话不一致。");
            var h = ReadHeartbeat(Path.Combine(ResourcePaths.Full(registry), project.Id + ".json"));
            if (h.Id != project.Id || !ResourcePaths.Comparer.Equals(h.ProjectPath, project.ProjectPath) || h.Busy)
                throw new ArgumentException("Unity 当前正在处理任务或工程会话已改变，请等待导入结束后刷新。");
            return h.ProcessStartTicks;
        }
        Validate(project);
        return project with { RecoveryValidation = Validate };
    }
    private static bool IsUnity(int pid, long ticks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.Equals("Unity", StringComparison.OrdinalIgnoreCase)
                && process.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch { return false; }
    }
}
