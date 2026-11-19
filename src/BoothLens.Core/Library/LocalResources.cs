using BoothLens.Core.Models;
using BoothLens.Core.Util;

namespace BoothLens.Core.Library;

public sealed class LocalResources
{
    // The configured directory is depth zero. Its children are depth one.
    public const int MaxDepth = 4;
    private const int MaxEntries = 20_000;
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
        { "Library", "Temp", "obj", "bin", ".git", "Logs", "ProjectSettings" };
    private const double AutoThreshold = 0.72;
    private const double ReviewThreshold = 0.45;
    private readonly ItemStore _store;

    public LocalResources(ItemStore store) => _store = store;

    public ScanResult Scan(string root)
    {
        var result = new ScanResult();
        string fullRoot;
        try
        {
            fullRoot = ResourcePaths.Full(root);
            ResourcePaths.RequireDirectory(fullRoot);
        }
        catch (Exception ex) when (ResourcePaths.IsPathError(ex))
        {
            result.Errors.Add(ex.Message);
            return result;
        }

        var candidates = _store.SnapshotForMatching(4000);
        var manual = new Dictionary<string, long>(ResourcePaths.Comparer);
        foreach (var (path, id) in _store.ListUserLinks())
        {
            if (!Path.IsPathFullyQualified(path)) continue;
            try { manual[ResourcePaths.Full(path)] = id; }
            catch (Exception ex) when (ResourcePaths.IsPathError(ex)) { }
        }

        var titleById = candidates.ToDictionary(item => item.Id, item => item.Title);
        var idExists = new Dictionary<long, bool>();
        var visited = 0;
        var truncated = false;
        void ScanEntry(string path, int depth)
        {
            if (visited++ >= MaxEntries)
            {
                truncated = true;
                return;
            }
            try
            {
                var name = Path.GetFileName(path);
                if (depth > 0 && name.StartsWith('.')) return;
                var attributes = ResourcePaths.NoLinks(path);
                var folder = (attributes & FileAttributes.Directory) != 0;
                if (depth > 0 && folder && SkipFolders.Contains(name)) return;
                var kind = folder ? "folder" : Classify(name);
                if (depth > 0 && ((folder && depth <= 2) || (!folder && kind != "other") || manual.ContainsKey(path)))
                {
                    var previous = _store.GetLocalFile(path);
                    var file = new LocalFile
                    {
                        Path = path, RootPath = fullRoot, Name = name, Kind = kind,
                        Size = folder ? 0 : new FileInfo(path).Length,
                        Mtime = File.GetLastWriteTimeUtc(path).ToString("O")
                    };

                    _store.UpsertLocalFile(file);
                    if (previous is null) result.New++;
                    result.Scanned++;
                    if (manual.TryGetValue(path, out var manualId) && previous?.Status != "ignored")
                        _store.SetFileLink(path, manualId, 1, "manual", "auto");
                    else if (previous?.MatchedBy is not "manual" && previous?.Status is not "ignored")
                    {
                        var (id, confidence, matchedBy, status) = Match(folder ? name : Path.GetFileNameWithoutExtension(name));
                        _store.SetFileLink(path, id, confidence, matchedBy, status, preserveManual: true);
                    }

                    switch (_store.GetLocalFile(path)?.Status)
                    {
                        case "auto": result.Auto++; break;
                        case "review": result.Review++; break;
                        case "unknown": result.Unknown++; break;
                    }
                }

                if (folder && depth < MaxDepth)
                    foreach (var child in Directory.EnumerateFileSystemEntries(path))
                    {
                        ScanEntry(child, depth + 1);
                        if (truncated) break;
                    }
            }
            catch (Exception ex) when (ResourcePaths.IsPathError(ex))
            {
                result.Errors.Add($"{path}: {ex.Message}");
            }
        }

        (long? Id, double? Confidence, string? MatchedBy, string Status) Match(string name)
        {
            var ids = TextNorm.ExtractDigitCandidates(name).Distinct().Where(id =>
            {
                if (titleById.ContainsKey(id)) return true;
                if (!idExists.TryGetValue(id, out var found)) idExists[id] = found = _store.Exists(id);
                return found;
            }).Take(2).ToArray();
            if (ids.Length == 1)
            {
                var id = ids[0];
                var score = TextNorm.Score(name, titleById.GetValueOrDefault(id) ?? _store.Get(id)?.Title);
                return (id, score >= ReviewThreshold ? 1 : Math.Max(score, ReviewThreshold),
                    "id_in_name", score >= ReviewThreshold ? "auto" : "review");
            }
            var fingerprint = TextNorm.Fingerprint(name);
            if (fingerprint.Length == 0) return (null, null, null, "unknown");
            var bigrams = TextNorm.Bigrams(fingerprint);
            long? best = null;
            double bestScore = 0, runnerUp = 0;
            foreach (var candidate in candidates)
            {
                var score = TextNorm.Jaccard(bigrams, candidate.Bigrams);
                if (fingerprint == candidate.Norm) score = 1;
                else if ((fingerprint.Length >= 4 && candidate.Norm.Contains(fingerprint, StringComparison.Ordinal)) ||
                         (candidate.Norm.Length >= 4 && fingerprint.Contains(candidate.Norm, StringComparison.Ordinal)))
                    score = Math.Max(score, 0.82);
                if (score > bestScore)
                {
                    runnerUp = bestScore;
                    bestScore = score;
                    best = candidate.Id;
                }
                else runnerUp = Math.Max(runnerUp, score);
            }

            if (bestScore < ReviewThreshold) return (null, null, null, "unknown");
            var automatic = bestScore >= AutoThreshold && bestScore - runnerUp >= 0.08 && ids.Length < 2;
            return (best, bestScore, "title_fuzzy", automatic ? "auto" : "review");
        }

        ScanEntry(fullRoot, 0);
        if (truncated) result.Errors.Add($"已达到 {MaxEntries} 个路径的扫描上限。");
        return result;
    }

    private static string Classify(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".zip" => "zip",
        ".unitypackage" => "unitypackage",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff" or ".psd" or ".tga" or ".svg" or ".dds" or ".exr" => "image",
        _ => "other"
    };
}

// Both discovery and import must reject link components, not just the final file.
internal static class ResourcePaths
{
    internal static readonly StringComparer Comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string Full(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请指定本地路径。");
        if (OperatingSystem.IsWindows() && (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)))
            throw new ArgumentException("不支持设备路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static FileAttributes NoLinks(string path)
    {
        var root = Path.GetPathRoot(path) ?? throw new ArgumentException("无效路径。");
        var current = root;
        var attributes = Check(current);
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0) continue;
            current = Path.Combine(current, part);
            attributes = Check(current);
        }
        return attributes;

        static FileAttributes Check(string part)
        {
            var value = File.GetAttributes(part);
            if ((value & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new IOException($"不支持链接、重解析点或设备：{part}");
            return value;
        }
    }

    internal static void RequireDirectory(string path)
    {
        if ((NoLinks(path) & FileAttributes.Directory) == 0) throw new IOException($"不是目录：{path}");
    }

    internal static bool Under(string directory, string path) => path.StartsWith(Path.EndsInDirectorySeparator(directory)
        ? directory : directory + Path.DirectorySeparatorChar, Comparison);

    internal static bool IsPathError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
