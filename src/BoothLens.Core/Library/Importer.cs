using System.IO.Compression;
using System.Text.Json;
using BoothLens.Core.Models;
using BoothLens.Core.Util;

namespace BoothLens.Core.Library;

public sealed class Importer(AppPaths paths, ItemStore store)
{
    private const int MaxEntries = 20_000;
    private const long MaxBytes = 8L * 1024 * 1024 * 1024;

    public ImportJob Import(string path, string? mode, string? project)
    {
        path = ResourcePaths.Full(path);
        mode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant();
        if (mode is not ("auto" or "copy" or "unitypackage" or "extract"))
            throw new ArgumentException("Unknown import mode.", nameof(mode));
        var local = store.GetLocalFile(path) ?? throw new ArgumentException("Source is not registered.");
        if (local.ItemId is not long itemId || local.Status == "ignored" || store.Get(itemId) is not { } item)
            throw new ArgumentException("Source must be bound to an existing item.");
        var id = store.CreateJob(itemId, path, mode);
        string? target = null;
        try
        {
            var attributes = ResourcePaths.NoLinks(path);
            if (local.RootPath is { Length: > 0 } root && !ResourcePaths.Comparer.Equals(ResourcePaths.Full(root), path) && !ResourcePaths.Under(ResourcePaths.Full(root), path))
                throw new IOException("Source is outside its registered root.");
            project = string.IsNullOrWhiteSpace(project) ? null : ResourcePaths.Full(project);
            if (project != null)
            {
                ResourcePaths.RequireDirectory(project);
                ResourcePaths.RequireDirectory(Path.Combine(project, "Assets"));
            }
            var stage = Path.Combine(paths.StagingDir, id.ToString());
            ResourcePaths.RequireDirectory(ResourcePaths.Full(paths.StagingDir));
            FreshDirectory(stage);
            var content = Path.Combine(stage, "content");
            FreshDirectory(content);
            var isFolder = (attributes & FileAttributes.Directory) != 0;
            var extension = isFolder ? "" : Path.GetExtension(path).ToLowerInvariant();
            string? package = null;
            if (extension == ".zip")
            {
                Extract(path, content);
                var packages = Directory.GetFiles(content, "*", SearchOption.AllDirectories)
                    .Where(p => Path.GetExtension(p).Equals(".unitypackage", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (packages.Length == 1) package = packages[0];
                if (packages.Length > 1)
                {
                    if (mode == "unitypackage") throw new IOException("ZIP contains multiple unitypackages; choose one explicitly.");
                    store.UpdateJob(id, "staged", "Multiple unitypackages; staged without selecting a package.", content);
                    return store.GetJob(id)!;
                }
                if (package == null && mode == "unitypackage") throw new IOException("ZIP contains no unitypackage.");
            }
            else
            {
                if (mode == "extract") throw new IOException("Extract mode requires a ZIP source.");
                if (mode == "unitypackage" && extension != ".unitypackage") throw new IOException("Source is not a unitypackage.");
                long bytes = 0;
                var entries = 0;
                if (isFolder) CopyTree(path, content, ref entries, ref bytes);
                else
                {
                    var stagedFile = Path.Combine(content, Path.GetFileName(path));
                    CopyFile(path, stagedFile, ref entries, ref bytes);
                    if (extension == ".unitypackage") package = stagedFile;
                    if (File.Exists(path + ".meta")) CopyFile(path + ".meta", stagedFile + ".meta", ref entries, ref bytes);
                }
            }
            target = package ?? content;
            if (project == null || (extension == ".zip" && package == null && mode != "extract"))
                store.UpdateJob(id, "staged", "Staged; no project assets were changed.", target);
            else if (package != null)
            {
                Queue(project, id, "unitypackage", package, "");
                store.UpdateJob(id, "queued", "已提交给 Unity；请切换到 Unity 的 Import Unity Package 窗口确认 Import，完成后回传结果。", project);
            }
            else
            {
                var relative = "Assets/_BoothItems/" + SafeName.FolderName(itemId, item.Title) + "__job" + id;
                target = Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar));
                EnsureDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target) || Directory.Exists(target) || File.Exists(target + ".meta"))
                    throw new IOException("Target already exists.");
                // Publish only a complete copy; the original staging tree remains available.
                var pending = target + ".pending-" + Guid.NewGuid().ToString("N");
                FreshDirectory(pending);
                long bytes = 0;
                var entries = 0;
                CopyTree(content, pending, ref entries, ref bytes);
                Directory.Move(pending, target);
                Queue(project, id, "refresh", target, relative);
                store.UpdateJob(id, "queued", "Files published; waiting for Unity refresh.", target);
            }
        }
        catch (Exception ex) when (ResourcePaths.IsPathError(ex) || ex is InvalidDataException)
        {
            store.UpdateJob(id, "failed", ex.Message, target);
        }
        return store.GetJob(id)!;
    }

    public sealed record ResetPendingResult(string ProjectId, IReadOnlyList<long> CancelledJobs, string Message);
    private sealed record PendingRecord(string File, string Folder, long Id, byte[] Content, bool Archived);
    private const string ResetMessage = "用户清理了跨进程遗留及排队的导入记录；未删除源文件或项目资源，请按需手动重新提交。";

    /// <summary>Cancel records only. No source/Assets access, replay, or package import occurs here.</summary>
    public ResetPendingResult ResetPending(UnityEditors.OpenProject project)
    {
        var validate = project.RecoveryValidation ?? throw new ArgumentException("请先核验 Unity 在线会话后再清理记录。");
        var start = validate(project);
        var projectPath = ResourcePaths.Full(project.ProjectPath);
        ResourcePaths.RequireDirectory(projectPath);
        ResourcePaths.RequireDirectory(Path.Combine(projectPath, "Assets"));
        var root = Path.Combine(projectPath, "Library", "BoothLens");
        if (!Directory.Exists(root)) return new(project.Id, Array.Empty<long>(), "没有待处理的导入记录。");
        ResourcePaths.RequireDirectory(root);
        var records = new List<PendingRecord>();
        long recordBytes = 0;
        // Preflight the entire batch before moving anything. Old running records remain a bridge barrier
        // until every queued record has been archived, so clearing the blockage never replays the queue.
        foreach (var folder in new[] { "queue", "running" })
        {
            ReadRecords(Path.Combine(root, folder), folder, false);
            ReadRecords(Path.Combine(root, "cancelled", folder), folder, true);
        }
        if (records.Any(r => !r.Archived && r.Folder == "queue") && !records.Any(r => !r.Archived && r.Folder == "running"))
            throw new ArgumentException("仅有排队任务，Unity 可能正在接收任务；请等待处理或在 Unity 中确认。");
        var done = Path.Combine(root, "done");
        if (Directory.Exists(done)) ResourcePaths.RequireDirectory(done);
        foreach (var record in records)
        {
            var receipt = Path.Combine(done, record.Id + ".json");
            if (!File.Exists(receipt)) continue;
            using var json = JsonDocument.Parse(UnityEditors.ReadLimited(receipt, 65536));
            var value = json.RootElement;
            if (!value.TryGetProperty("jobId", out var id) || !id.TryGetInt64(out var received) || received != record.Id ||
                !value.TryGetProperty("state", out var state) || state.GetString() != "failed")
                throw new IOException("任务已有完成回执，请先刷新任务状态再清理。");
        }
        foreach (var record in records.Where(r => !r.Archived))
        {
            var archive = Path.Combine(root, "cancelled", record.Folder, record.Id + ".json");
            if (File.Exists(archive) || Directory.Exists(archive))
                throw new IOException("待处理任务归档位置已存在，请检查 BoothLens 记录。");
        }
        var cancelled = new HashSet<long>();
        foreach (var record in records.OrderBy(r => r.Folder == "queue" ? 0 : 1).ThenBy(r => r.Id))
        {
            if (validate(project) != start) throw new ArgumentException("Unity 会话已改变，请刷新后重试。");
            CheckRunning();
            var archiveDirectory = Path.Combine(root, "cancelled", record.Folder);
            EnsureDirectory(archiveDirectory);
            var archive = Path.Combine(archiveDirectory, record.Id + ".json");
            if (!record.Archived)
            {
                // Re-read immediately before moving: an updated/replaced record must not be cancelled.
                if (!UnityEditors.ReadLimited(record.File, 65536).SequenceEqual(record.Content))
                    throw new IOException("待处理任务发生变化，请刷新后重试。");
                if (record.Folder == "running" && File.GetLastWriteTimeUtc(record.File).Ticks >= start)
                    throw new ArgumentException("检测到当前 Unity 会话的运行任务，请在 Unity 中结束当前导入。");
                if (File.Exists(archive))
                {
                    ResourcePaths.NoLinks(archive);
                    // Existing archives are immutable; never overwrite a record from another attempt.
                    throw new IOException("任务归档已存在且待处理记录仍在，请检查 BoothLens 记录。");
                }
                File.Move(record.File, archive);
            }
            if (store.GetJob(record.Id)?.State != "queued") continue;
            EnsureDirectory(done);
            var receipt = Path.Combine(done, record.Id + ".json");
            if (!File.Exists(receipt))
            {
                var temporary = receipt + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(output, new { jobId = record.Id, state = "failed", message = ResetMessage });
                File.Move(temporary, receipt);
            }
            cancelled.Add(record.Id);
        }
        // Use the same receipt path as ordinary Unity completion, rather than directly editing job state.
        Poll(projectPath);
        return new(project.Id, cancelled.OrderBy(id => id).ToArray(), cancelled.Count == 0 ? "没有待处理的导入记录。" : ResetMessage);

        void CheckRunning()
        {
            var running = Path.Combine(root, "running");
            if (!Directory.Exists(running)) return;
            ResourcePaths.RequireDirectory(running);
            foreach (var file in Directory.EnumerateFiles(running, "*.json").Take(MaxEntries + 1))
            {
                ResourcePaths.NoLinks(file);
                if (File.GetLastWriteTimeUtc(file).Ticks >= start || !records.Any(r => !r.Archived && r.Folder == "running" && r.File == file))
                    throw new ArgumentException("检测到当前 Unity 会话的运行任务或新增任务，请在 Unity 中结束当前导入。");
            }
        }
        void ReadRecords(string directory, string folder, bool archived)
        {
            if (!Directory.Exists(directory)) return;
            ResourcePaths.RequireDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (records.Count >= MaxEntries) throw new IOException("待处理记录数量超过清理上限。");
                var content = UnityEditors.ReadLimited(file, 65536);
                recordBytes += content.Length;
                if (recordBytes > 16 * 1024 * 1024) throw new IOException("待处理记录总大小超过清理上限。");
                var id = UnityEditors.JobId(file) ?? throw new IOException("待处理任务文件名无效。");
                using var json = JsonDocument.Parse(content);
                var value = json.RootElement;
                if (!value.TryGetProperty("jobId", out var jobId) || !jobId.TryGetInt64(out var received) || received != id ||
                    !value.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String || mode.GetString() is not ("unitypackage" or "refresh"))
                    throw new IOException("待处理任务 ID 或类型无效。");
                if (!archived && folder == "running" && File.GetLastWriteTimeUtc(file).Ticks >= start)
                    throw new ArgumentException("检测到当前 Unity 会话的运行任务，请在 Unity 中结束当前导入。");
                var job = store.GetJob(id) ?? throw new IOException("待处理记录未关联到本机导入任务。");
                if (string.IsNullOrWhiteSpace(job.TargetPath)) throw new IOException("待处理任务缺少目标工程。");
                var target = ResourcePaths.Full(job.TargetPath);
                if (mode.GetString() == "unitypackage" ? !ResourcePaths.Comparer.Equals(target, projectPath)
                    : !ResourcePaths.Under(Path.Combine(projectPath, "Assets"), target))
                    throw new IOException("待处理任务不属于所选工程。");
                if (records.Any(r => r.Id == id)) throw new IOException("待处理任务 ID 重复，请检查 BoothLens 记录。");
                records.Add(new(file, folder, id, content, archived));
            }
        }
    }

    public void Poll(string? project)
    {
        if (string.IsNullOrWhiteSpace(project)) return;
        project = ResourcePaths.Full(project);
        var done = Path.Combine(project, "Library", "BoothLens", "done");
        if (!Directory.Exists(done)) return;
        ResourcePaths.RequireDirectory(done);
        foreach (var receipt in Directory.EnumerateFiles(done, "*.json").Take(MaxEntries))
        {
            try
            {
                ResourcePaths.NoLinks(receipt);
                if (UnityEditors.JobId(receipt) is not long id) continue;
                var job = store.GetJob(id);
                if (job?.State != "queued" || job.TargetPath == null) continue;
                var target = ResourcePaths.Full(job.TargetPath);
                if (!ResourcePaths.Comparer.Equals(target, project) && !ResourcePaths.Under(project, target)) continue;
                using var json = JsonDocument.Parse(UnityEditors.ReadLimited(receipt, 65536));
                var root = json.RootElement;
                if (!root.TryGetProperty("jobId", out var jobId) || !jobId.TryGetInt64(out var receivedId) || receivedId != id) continue;
                if (!root.TryGetProperty("state", out var stateValue) || stateValue.ValueKind != JsonValueKind.String) continue;
                var state = stateValue.GetString();
                if (state is not ("done" or "failed" or "unsupported")) continue;
                var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                store.UpdateJob(id, state, message);
                var archive = Path.Combine(done, "archive");
                EnsureDirectory(archive);
                File.Move(receipt, Path.Combine(archive, id + "_" + Guid.NewGuid().ToString("N") + ".json"));
            }
            catch (Exception ex) when (ResourcePaths.IsPathError(ex) || ex is JsonException or InvalidOperationException) { }
        }
    }

    private static void Queue(string project, long id, string mode, string source, string targetFolder)
    {
        var queue = Path.Combine(project, "Library", "BoothLens", "queue");
        EnsureDirectory(queue);
        var file = Path.Combine(queue, id + ".json");
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, new { jobId = id, mode, source, targetFolder });
        File.Move(temporary, file); // No overwrite; readers only see complete JSON.
    }

    private static void EnsureDirectory(string path)
    {
        path = ResourcePaths.Full(path);
        if (Directory.Exists(path)) { ResourcePaths.RequireDirectory(path); return; }
        EnsureDirectory(Path.GetDirectoryName(path) ?? throw new IOException("Missing directory root."));
        Directory.CreateDirectory(path);
        ResourcePaths.RequireDirectory(path);
    }

    private static void FreshDirectory(string path)
    {
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Staging or publish directory already exists.");
        EnsureDirectory(path);
    }

    private static void CopyTree(string source, string target, ref int entries, ref long bytes, int depth = 0)
    {
        ResourcePaths.RequireDirectory(source);
        if (depth > 128) throw new IOException("Source directory nesting limit exceeded.");
        foreach (var child in Directory.EnumerateFileSystemEntries(source))
        {
            if (Path.GetFileName(child).Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
            var attributes = ResourcePaths.NoLinks(child);
            var destination = Path.Combine(target, Path.GetFileName(child));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (++entries > MaxEntries) throw new IOException("Entry limit exceeded.");
                FreshDirectory(destination);
                CopyTree(child, destination, ref entries, ref bytes, depth + 1);
            }
            else CopyFile(child, destination, ref entries, ref bytes);
        }
    }

    private static void CopyFile(string source, string target, ref int entries, ref long bytes)
    {
        ResourcePaths.NoLinks(source);
        bytes += new FileInfo(source).Length;
        if (++entries > MaxEntries || bytes > MaxBytes) throw new IOException("Import size or entry limit exceeded.");
        File.Copy(source, target, false);
    }

    private static void Extract(string source, string target)
    {
        using var zip = ZipFile.OpenRead(source);
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("ZIP entry limit exceeded.");
        long bytes = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.TrimEnd('/').Split('/');
            if (Path.IsPathRooted(name) || parts.Any(p => p is ".." or "." or "" || p.Contains(':') || p.EndsWith('.') || p.EndsWith(' ')))
                throw new InvalidDataException("Unsafe ZIP entry path.");
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ZIP links and special files are unsupported.");
            bytes += entry.Length;
            if (bytes > MaxBytes) throw new InvalidDataException("ZIP expanded size limit exceeded.");
            if (parts.Any(p => p.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))) continue;
            var destination = ResourcePaths.Full(Path.Combine(target, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!ResourcePaths.Under(target, destination)) throw new InvalidDataException("ZIP entry escapes staging.");
            if (name.EndsWith('/')) EnsureDirectory(destination);
            else
            {
                EnsureDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long written = 0;
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    written += count;
                    if (written > entry.Length) throw new InvalidDataException("ZIP entry exceeds declared size.");
                    output.Write(buffer, 0, count);
                }
                if (written != entry.Length) throw new InvalidDataException("ZIP entry size mismatch.");
            }
        }
    }
}
