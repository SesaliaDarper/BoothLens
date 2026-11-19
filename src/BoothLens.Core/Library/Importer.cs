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
                store.UpdateJob(id, "queued", "Waiting for Unity package import.", project);
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
                if (!long.TryParse(Path.GetFileNameWithoutExtension(receipt), out var id) || new FileInfo(receipt).Length > 65536) continue;
                var job = store.GetJob(id);
                if (job?.State != "queued" || job.TargetPath == null) continue;
                var target = ResourcePaths.Full(job.TargetPath);
                if (!ResourcePaths.Comparer.Equals(target, project) && !ResourcePaths.Under(project, target)) continue;
                using var json = JsonDocument.Parse(File.ReadAllText(receipt));
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
