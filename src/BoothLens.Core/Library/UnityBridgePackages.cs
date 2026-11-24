using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BoothLens.Core.Library;

/// <summary>Explicit, offline embedded-UPM installation. Never edits manifest.json or arbitrary Assets.</summary>
public sealed class UnityBridgePackages
{
    public const string PackageName = "com.boothlens.bridge";
    public const string OwnershipFile = ".boothlens-managed.json";
    private const int Limit = 128 * 1024;
    private readonly Dictionary<string, byte[]> files;
    private readonly HashSet<string> legacyHashes;
    private readonly Func<IReadOnlyList<UnityEditors.OpenProject>>? editors;
    private readonly Action<string>? checkpoint;
    private readonly object gate = new();
    private readonly string version;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private static readonly Regex RelayType = new(@"\bnamespace\s+BoothLens\s*\.\s*Editor\b[\s\S]*\bclass\s+BoothRelay\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public sealed record Status(string ProjectPath, bool Installed, string? Version, bool Managed, IReadOnlyList<string> LegacyScripts,
        string State, string Message, string? BackupPath = null);
    private sealed record Ownership(string Owner, string Version, Dictionary<string, string> Files);
    private sealed record Inspection(Status Status, List<string> Legacy, bool PackageExists);

    // checkpoint is a deterministic transaction-fault hook for isolated tests, never used by the App.
    public UnityBridgePackages(Dictionary<string, byte[]> packageFiles, IEnumerable<string> knownLegacySha256,
        Func<IReadOnlyList<UnityEditors.OpenProject>>? editors = null, Action<string>? checkpoint = null)
    {
        files = new(StringComparer.Ordinal);
        foreach (var (name, bytes) in packageFiles)
        {
            if (!SafeRelative(name) || name == OwnershipFile || bytes.Length > Limit) throw new ArgumentException("桥接包资源路径或大小异常。");
            files.Add(name, bytes.ToArray());
        }
        if (!files.ContainsKey("package.json") || !files.ContainsKey("Editor/BoothRelay.cs") || !files.ContainsKey("Editor/BoothLens.Editor.asmdef"))
            throw new ArgumentException("桥接包资源不完整。");
        using var doc = JsonDocument.Parse(files["package.json"]);
        if (doc.RootElement.GetProperty("name").GetString() != PackageName) throw new ArgumentException("桥接包名称异常。");
        version = doc.RootElement.GetProperty("version").GetString() ?? throw new ArgumentException("桥接包缺少版本。");
        legacyHashes = new(knownLegacySha256, StringComparer.OrdinalIgnoreCase);
        this.editors = editors; this.checkpoint = checkpoint;
    }

    public Status GetStatus(string projectPath) { lock (gate) return Inspect(projectPath).Status; }
    public Status Install(string projectPath) { lock (gate) return Mutate(projectPath, uninstall: false); }
    public Status Uninstall(string projectPath) { lock (gate) return Mutate(projectPath, uninstall: true); }

    private Inspection Inspect(string input)
    {
        var project = input ?? "";
        var legacy = new List<string>();
        bool exists = false, managed = false; string? installedVersion = null;
        Status Result(string state, string message) => new(project, exists, installedVersion, managed, legacy.ToArray(), state, message);
        try
        {
            project = ValidateProject(project);
            var package = Path.Combine(project, "Packages", PackageName);
            exists = Directory.Exists(package);
            // A named file or dangling link is also a conflict; do not treat it as an empty install target.
            if (Exists(package)) ResourcePaths.RequireDirectory(package);
            var manifest = Path.Combine(project, "Packages", "manifest.json");
            if (Exists(manifest))
            {
                using var doc = JsonDocument.Parse(Read(manifest));
                if (doc.RootElement.TryGetProperty("dependencies", out var dependencies) && dependencies.TryGetProperty(PackageName, out _))
                    return new(Result("conflict", "工程 manifest.json 已声明同名 UPM 依赖，请先手动解决依赖冲突；BoothLens 不修改 manifest。"), legacy, exists);
            }
            ScanAssets(project, legacy);
            if (exists)
            {
                // Validate every path, including extras, before archiving the entire package intact.
                TreeFiles(package, 4096).ToList();
                var marker = Path.Combine(package, OwnershipFile);
                if (!File.Exists(marker)) return new(Result("conflict", "同名包缺少 BoothLens ownership 标记，保留原包。"), legacy, true);
                var owner = JsonSerializer.Deserialize<Ownership>(Read(marker), Json);
                if (owner?.Owner != PackageName || owner.Files == null || owner.Files.Count is < 3 or > 128 ||
                    !owner.Files.ContainsKey("package.json") || !owner.Files.ContainsKey("Editor/BoothRelay.cs") || !owner.Files.ContainsKey("Editor/BoothLens.Editor.asmdef"))
                    return new(Result("conflict", "包 ownership 记录异常，保留原包。"), legacy, true);
                foreach (var (relative, hash) in owner.Files)
                    if (!SafeRelative(relative) || relative == OwnershipFile || !File.Exists(Path.Combine(package, relative)) || Hash(Read(Path.Combine(package, relative))) != hash.ToUpperInvariant())
                        return new(Result("conflict", "桥接包受管文件已修改或缺失，请先手动保存更改；原包保持不动。"), legacy, true);
                using var pkg = JsonDocument.Parse(Read(Path.Combine(package, "package.json")));
                installedVersion = pkg.RootElement.GetProperty("version").GetString();
                if (pkg.RootElement.GetProperty("name").GetString() != PackageName || installedVersion != owner.Version)
                    return new(Result("conflict", "桥接包身份与 ownership 记录不一致。"), legacy, true);
                managed = true;
                var exact = owner.Files.Count == files.Count && files.All(f => owner.Files.TryGetValue(f.Key, out var hash) && hash.Equals(Hash(f.Value), StringComparison.OrdinalIgnoreCase));
                return new(Result(exact && legacy.Count == 0 ? "installed" : "update-available", exact && legacy.Count == 0 ?
                    "嵌入式桥接包已安装。安装状态不代表编辑器在线，请等待 Unity 编译完成并刷新工程列表。" : "可更新嵌入式桥接包；旧包与已识别旧脚本将完整归档。"), legacy, true);
            }
            return new(Result("not-installed", "尚未安装桥接包。请先退出 Play Mode，等待 Unity 编译和导入结束后手动安装；当前未确认编辑器连接。"), legacy, false);
        }
        catch (Exception ex) when (ResourcePaths.IsPathError(ex) || ex is JsonException or InvalidOperationException or KeyNotFoundException or RegexMatchTimeoutException)
        { return new(Result(project == input && !Directory.Exists(project) ? "invalid-project" : "conflict", ex.Message), legacy, exists); }
    }

    private Status Mutate(string input, bool uninstall)
    {
        var inspection = Inspect(input); var status = inspection.Status;
        if (status.State is "conflict" or "invalid-project") return status;
        var active = editors?.Invoke().FirstOrDefault(e => ResourcePaths.Comparer.Equals(e.ProjectPath, status.ProjectPath));
        if (active?.Busy == true) return status with { State = "busy", Message = (active.BlockReason ?? "Unity 正忙，请等待编辑器空闲。") + " 若为遗留任务，请先在待处理恢复中解决并刷新，安装器不会自动清除记录。" };
        if (uninstall && !inspection.PackageExists) return status with { State = "not-installed", Message = "未安装受管包；Assets 中的脚本保持原样。" };
        if (!uninstall && status.State == "installed") return status;
        var project = status.ProjectPath;
        var package = Path.Combine(project, "Packages", PackageName);
        var workspace = Path.Combine(project, "Library", "BoothLens");
        string? backup = null, staging = null; bool oldMoved = false, newMoved = false;
        var moved = new List<(string Source, string Target)>();
        try
        {
            EnsureDirectory(workspace);
            var backups = Path.Combine(workspace, "bridge-backups"); EnsureDirectory(backups);
            backup = Path.Combine(backups, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + "-" + Guid.NewGuid().ToString("N"));
            EnsureDirectory(backup);
            if (!uninstall)
            {
                var stageRoot = Path.Combine(workspace, "bridge-staging"); EnsureDirectory(stageRoot);
                staging = Path.Combine(stageRoot, Guid.NewGuid().ToString("N")); EnsureDirectory(staging);
                foreach (var (relative, bytes) in files)
                {
                    var target = Path.Combine(staging, relative); EnsureDirectory(Path.GetDirectoryName(target)!);
                    using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes); stream.Flush(true);
                }
                var owner = new Ownership(PackageName, version, files.ToDictionary(f => f.Key, f => Hash(f.Value)));
                File.WriteAllBytes(Path.Combine(staging, OwnershipFile), JsonSerializer.SerializeToUtf8Bytes(owner, Json));
                checkpoint?.Invoke("staged");
            }
            // Recheck immediately before moves, including Assets and managed-file hashes.
            var current = Inspect(project);
            if (current.Status.State is "conflict" or "invalid-project" || current.PackageExists != inspection.PackageExists || !current.Legacy.SequenceEqual(inspection.Legacy))
                throw new IOException("工程内容已改变，请刷新安装状态后重试。");
            var currentEditor = editors?.Invoke().FirstOrDefault(e => ResourcePaths.Comparer.Equals(e.ProjectPath, project));
            if (currentEditor?.Busy == true) throw new IOException("Unity 在安装期间变为忙碌，请先解决当前或遗留任务后重试。");
            if (inspection.PackageExists)
            {
                ResourcePaths.RequireDirectory(package);
                Directory.Move(package, Path.Combine(backup, "package")); oldMoved = true;
                var packageMeta = package + ".meta";
                if (Exists(packageMeta)) MoveFile(packageMeta, Path.Combine(backup, "package.meta"), moved);
            }
            checkpoint?.Invoke("old-package-archived");
            if (!uninstall)
            {
                foreach (var relative in inspection.Legacy)
                {
                    var source = Path.Combine(project, relative);
                    if (!legacyHashes.Contains(Hash(Read(source)))) throw new IOException("旧脚本内容已改变，安装已回滚。");
                    MoveFile(source, Path.Combine(backup, "legacy", relative), moved);
                    if (Exists(source + ".meta")) MoveFile(source + ".meta", Path.Combine(backup, "legacy", relative + ".meta"), moved);
                }
                checkpoint?.Invoke("legacy-archived");
                ResourcePaths.RequireDirectory(Path.Combine(project, "Packages"));
                if (Exists(package)) throw new IOException("包目标在安装期间出现冲突。");
                Directory.Move(staging!, package); newMoved = true; staging = null;
                checkpoint?.Invoke("package-installed");
            }
            return new(project, !uninstall, uninstall ? null : version, !uninstall, uninstall ? inspection.Legacy.ToArray() : Array.Empty<string>(), uninstall ? "not-installed" : "installed",
                uninstall ? "受管桥接包已完整移至备份，用户附加文件一并保留；未更改 manifest 或 Assets。" : "桥接包已安装；请等待 Unity 编译结束再刷新工程列表。编辑器是否在线以心跳为准。", backup);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try
            {
                // Do not delete a partially published package: archive it too.
                if (newMoved) Directory.Move(package, Path.Combine(backup!, "failed-new-package"));
                if (oldMoved) Directory.Move(Path.Combine(backup!, "package"), package);
                foreach (var move in moved.AsEnumerable().Reverse()) File.Move(move.Target, move.Source);
            }
            catch (Exception rollback) { throw new IOException($"安装失败且回滚需人工检查。备份：{backup}；错误：{ex.Message}；回滚：{rollback.Message}", rollback); }
            return status with { State = "conflict", Message = "操作已回滚，原包与旧脚本已恢复：" + ex.Message, BackupPath = backup };
        }
        finally
        {
            // Staging contains only bytes generated by this transaction; never delete a user package.
            if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private void ScanAssets(string project, List<string> legacy)
    {
        const int scriptLimit = 8 * 1024 * 1024;
        const long scanLimit = 64L * 1024 * 1024;
        long scanned = 0;
        foreach (var file in TreeFiles(Path.Combine(project, "Assets"), 100000))
        {
            if (!Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var named = Path.GetFileName(file).Equals("BoothRelay.cs", StringComparison.OrdinalIgnoreCase);
            if (new FileInfo(file).Length > scriptLimit) throw new IOException("Assets 含超过 8MiB 的 C# 文件，完整同名类型检查需手动处理后重试：" + Path.GetRelativePath(project, file));
            var bytes = UnityEditors.ReadLimited(file, scriptLimit);
            scanned += bytes.Length;
            if (scanned > scanLimit) throw new IOException("Assets C# 检查超过 64MiB 总预算，请整理后重试：" + Path.GetRelativePath(project, file));
            if (named && legacyHashes.Contains(Hash(bytes)))
            {
                var meta = file + ".meta"; if (Exists(meta)) ResourcePaths.NoLinks(meta);
                legacy.Add(Path.GetRelativePath(project, file));
            }
            else if (named || RelayType.IsMatch(Encoding.UTF8.GetString(bytes)) || RelayType.IsMatch(Encoding.Unicode.GetString(bytes)))
                throw new IOException("发现未识别的同名脚本或 BoothLens.Editor.BoothRelay 类型，保留原文件：" + Path.GetRelativePath(project, file));
        }
        legacy.Sort(StringComparer.Ordinal);
    }
    private static string ValidateProject(string input)
    {
        var project = ResourcePaths.Full(input);
        if (ResourcePaths.Comparer.Equals(project, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(project)!))) throw new ArgumentException("请选择 Unity 工程目录，而非文件系统根目录。");
        ResourcePaths.RequireDirectory(project);
        ResourcePaths.RequireDirectory(Path.Combine(project, "Assets"));
        ResourcePaths.RequireDirectory(Path.Combine(project, "Packages"));
        var versionFile = Path.Combine(project, "ProjectSettings", "ProjectVersion.txt");
        var text = Encoding.UTF8.GetString(Read(versionFile));
        var match = Regex.Match(text, @"(?:m_EditorVersion:\s*)?(\d{4})\.(\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success || int.Parse(match.Groups[1].Value) < 2022 || int.Parse(match.Groups[1].Value) == 2022 && int.Parse(match.Groups[2].Value) < 3)
            throw new ArgumentException("桥接包支持 Unity 2022.3 或更新版本，请先升级工程。安装器未修改工程。");
        // Validate existing ancestors of all future write locations before creating anything.
        CheckExisting(Path.Combine(project, "Library", "BoothLens", "bridge-backups"));
        CheckExisting(Path.Combine(project, "Library", "BoothLens", "bridge-staging"));
        var meta = Path.Combine(project, "Packages", PackageName + ".meta"); if (Exists(meta)) ResourcePaths.NoLinks(meta);
        return project;
    }
    private static IEnumerable<string> TreeFiles(string root, int limit)
    {
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0)); int count = 0;
        while (pending.Count != 0)
        {
            var (folder, depth) = pending.Pop(); ResourcePaths.RequireDirectory(folder);
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                if (++count > limit || depth > 64) throw new IOException("工程目录超过桥接检查上限，请整理后重试。");
                var attributes = ResourcePaths.NoLinks(entry);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push((entry, depth + 1));
                else yield return entry;
            }
        }
    }
    private static void CheckExisting(string path)
    {
        var current = path;
        while (!Exists(current)) current = Path.GetDirectoryName(current) ?? throw new IOException("路径没有有效父目录。");
        ResourcePaths.RequireDirectory(current);
    }
    private static void EnsureDirectory(string path) { CheckExisting(path); Directory.CreateDirectory(path); ResourcePaths.RequireDirectory(path); }
    private static bool Exists(string path) { try { File.GetAttributes(path); return true; } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } }
    private static byte[] Read(string file) => UnityEditors.ReadLimited(file, Limit);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool SafeRelative(string name) => !string.IsNullOrWhiteSpace(name) && !Path.IsPathRooted(name) && !name.Contains('\\') && !name.Contains(':') && name.Split('/').All(p => p.Length > 0 && p is not "." and not "..");
    private static void MoveFile(string source, string target, List<(string Source, string Target)> moved)
    { ResourcePaths.NoLinks(source); EnsureDirectory(Path.GetDirectoryName(target)!); File.Move(source, target); moved.Add((source, target)); }
}
