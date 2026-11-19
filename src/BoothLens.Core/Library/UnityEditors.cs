using System.Diagnostics;
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
    public sealed record OpenProject(string Id, string ProjectPath, string Name, int ProcessId, bool BridgeReady, bool IsActive, string LastSeen, bool Busy);
    public sealed class Heartbeat
    {
        public string? Id { get; set; }
        public string? ProjectPath { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartTicks { get; set; }
        public string? UpdatedUtc { get; set; }
        public bool IsActive { get; set; }
        public bool Busy { get; set; }
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
                ResourcePaths.NoLinks(file);
                if (new FileInfo(file).Length > 16384) continue;
                var h = JsonSerializer.Deserialize<Heartbeat>(File.ReadAllText(file), Json);
                if (h == null || string.IsNullOrWhiteSpace(h.Id) || Path.GetFileNameWithoutExtension(file) != h.Id ||
                    h.Id != $"{h.ProcessId}-{h.ProcessStartTicks}" || !DateTimeOffset.TryParse(h.UpdatedUtc, out var seen) ||
                    DateTimeOffset.UtcNow - seen > TimeSpan.FromSeconds(30) || seen - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(5) ||
                    !isAlive(h.ProcessId, h.ProcessStartTicks) || string.IsNullOrWhiteSpace(h.ProjectPath)) continue;
                var path = ResourcePaths.Full(h.ProjectPath); ResourcePaths.RequireDirectory(path);
                if (!Directory.Exists(Path.Combine(path, "Assets")) || !File.Exists(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt"))) continue;
                result.Add(new(h.Id, path, Path.GetFileName(path), h.ProcessId, true, h.IsActive, seen.ToString("O"), h.Busy));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { }
        }
        return result.DistinctBy(p => p.ProjectPath, ResourcePaths.Comparer).OrderByDescending(p => p.IsActive).ThenBy(p => p.Name).ToList();
    }
    public OpenProject Resolve(string? id)
    {
        var open = List();
        if (open.Count == 0) throw new ArgumentException("未发现已打开且桥接在线的 Unity 工程。请在目标工程安装新版 BoothRelay.cs，等待编译和心跳后刷新。");
        var project = string.IsNullOrWhiteSpace(id)
            ? open.Count == 1 ? open[0] : throw new ArgumentException("同时打开了多个 Unity 工程，请在导入窗口选择目标工程。")
            : open.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException("所选 Unity 工程已关闭或桥接已离线，请刷新工程列表。");
        if (project.Busy) throw new ArgumentException("所选 Unity 编辑器正在编译、刷新、运行或处理导入，请稍后重试。");
        return project;
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
