using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoothLens.Core.Booth;
using BoothLens.Core.Data;
using BoothLens.Core.Models;

namespace BoothLens.Core.Library;

/// <summary>Container-only discovery. Never changes source assets or downloads search-result covers.</summary>
public sealed class ContainerLibrary
{
    private readonly Db db;
    private readonly ItemStore store;
    private readonly Catalog catalog;
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private ScanStatus progress = new();
    private static readonly string[] Archives = [".tar.gz", ".tar.bz2", ".tar.xz", ".unitypackage", ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".tgz", ".tbz2", ".txz"];
    // Names are shallow boundary hints; once a product is reached, structural recursion stops.
    private static readonly HashSet<string> Categories = new(StringComparer.OrdinalIgnoreCase)
        { "角色", "人物", "衣服", "服装", "发型", "插件", "地图用", "装饰", "模型", "贴图", "材质", "成品家具", "VRC世界", "世界", "FBX模型", "avatars", "characters", "clothes", "clothing", "hair", "plugins", "maps", "accessories", "models" };
    private static readonly HashSet<string> AssetParts = new(StringComparer.OrdinalIgnoreCase)
        { "textures", "texture", "materials", "material", "uvmap", "uv", "psd", "readme", "read me", "meshes", "mesh", "shaders", "shader", "prefabs", "animations", "贴图", "材质" };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "__MACOSX", "Library", "Temp", "obj", "bin", "$RECYCLE.BIN", "System Volume Information" };
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public ContainerLibrary(Db db, ItemStore store, Catalog catalog, BoothClient booth)
    { this.db = db; this.store = store; this.catalog = catalog; _ = booth; EnsureSchema(); }

    public void EnsureSchema()
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
CREATE TABLE IF NOT EXISTS resource_meta (
 path TEXT PRIMARY KEY, root TEXT NOT NULL, category TEXT NOT NULL DEFAULT '',
 aliases_json TEXT NOT NULL DEFAULT '[]', members_json TEXT NOT NULL DEFAULT '[]',
 candidates_json TEXT, recognition_state TEXT NOT NULL DEFAULT 'unknown', error TEXT,
 active INTEGER NOT NULL DEFAULT 1, last_scan TEXT, updated_at TEXT);
CREATE INDEX IF NOT EXISTS ix_resource_root ON resource_meta(root,active);
CREATE TABLE IF NOT EXISTS resource_roots (path TEXT PRIMARY KEY, mode TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS folder_boundary (path TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('category','asset')));
CREATE TABLE IF NOT EXISTS resource_structure_version (path TEXT PRIMARY KEY, version INTEGER NOT NULL);
""";
        cmd.ExecuteNonQuery();
        using var columns = c.CreateCommand(); columns.CommandText = "PRAGMA table_info(resource_meta)";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = columns.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(1));
        foreach (var (name, definition) in new[] { ("local_cover_path", "TEXT"), ("retry_requested", "INTEGER NOT NULL DEFAULT 0"), ("last_scan_skipped", "INTEGER NOT NULL DEFAULT 0") })
            if (!names.Contains(name)) { using var alter = c.CreateCommand(); alter.CommandText = $"ALTER TABLE resource_meta ADD COLUMN {name} {definition}"; alter.ExecuteNonQuery(); }
    }

    public object Start(string root, string? mode, bool online = true, bool rebuildStructure = false)
    {
        root = ResourcePaths.Full(root); mode = Mode(mode);
        lock (gate)
        {
            if (worker is { IsCompleted: false }) return new { scanId = progress.ScanId, state = progress.State, busy = true };
            cancellation?.Dispose(); cancellation = new();
            progress = new() { ScanId = Guid.NewGuid().ToString("N"), State = "running", Root = root, Mode = mode };
            var token = cancellation.Token; var id = progress.ScanId;
            worker = Task.Run(() => Run(root, mode, online, id!, token, rebuildStructure));
            return new { scanId = id, state = "running", busy = false };
        }
    }
    public ScanStatus GetStatus() { lock (gate) return progress with { }; }
    public object Cancel() { lock (gate) { cancellation?.Cancel(); return new { scanId = progress.ScanId, state = progress.State }; } }
    public object Preview(string root, string? mode = null)
    {
        root = ResourcePaths.Full(root); mode = Mode(mode);
        var result = Discover(root, mode, CancellationToken.None);
        return new { root, mode, resolvedMode = result.ResolvedMode, detectionExplanation = result.Explanation, warnings = result.Warnings, needsMode = result.NeedsMode, discovered = result.Resources.Count, files = result.Resources, networkRequests = 0 };
    }
    private async Task Run(string root, string mode, bool online, string scanId, CancellationToken ct, bool rebuildStructure)
    {
        try
        {
            var discovery = Discover(root, mode, ct);
            var resources = discovery.Resources;
            var catalogRoot = CatalogRoot(root);
            lock (gate)
            {
                Execute("INSERT INTO resource_roots(path,mode) VALUES($p,$m) ON CONFLICT(path) DO UPDATE SET mode=excluded.mode", ("$p", catalogRoot), ("$m", ResourcePaths.Comparer.Equals(root, catalogRoot) ? discovery.ResolvedMode : RegisteredMode(catalogRoot) ?? "library"));
                // A newly scanned ancestor becomes the single canonical owner of its descendants.
                foreach (var registered in RegisteredRoots().Where(x => !ResourcePaths.Comparer.Equals(x.Path, catalogRoot) && Within(x.Path, catalogRoot)))
                    Execute("DELETE FROM resource_roots WHERE path=$p", ("$p", registered.Path));
            }
            lock (gate)
            {
                progress.Discovered = resources.Count; progress.ResolvedMode = discovery.ResolvedMode;
                progress.DetectionExplanation = discovery.Explanation; progress.Warnings = discovery.Warnings;
                progress.NeedsMode = discovery.NeedsMode;
            }
            var searches = new Dictionary<string, List<SearchHit>>(StringComparer.OrdinalIgnoreCase);
            var ids = new Dictionary<long, Item?>();
            foreach (var resource in resources)
            {
                ct.ThrowIfCancellationRequested();
                lock (gate) progress.CurrentPath = resource.Path;
                try
                {
                    if (resource.Skipped)
                    {
                        lock (gate)
                        {
                            // Classification repair never rewrites bindings, candidates or cached members.
                            Execute("UPDATE resource_meta SET root=$r,category=$c,active=1,last_scan=$s,last_scan_skipped=1 WHERE path=$p", ("$r", resource.Root), ("$c", resource.Category), ("$s", scanId), ("$p", resource.Path));
                            Execute("UPDATE local_files SET root_path=$r WHERE path=$p", ("$r", resource.Root), ("$p", resource.Path));
                            progress.Skipped++; progress.Processed++;
                            if (resource.RecognitionState == "bound") progress.Bound++;
                            else if (resource.RecognitionState == "review") progress.Review++;
                            else progress.Unknown++;
                        }
                        continue;
                    }
                    lock (gate)
                    {
                        var old = Meta(resource.Path);
                        resource.Candidates = old?.Candidates ?? [];
                        resource.SearchCached = old?.SearchCached ?? false;
                        var f = FileRecord(resource.Path, resource.Root);
                        store.UpsertLocalFile(f);
                        var preserved = resource.Aliases.Prepend(resource.Path).Select(store.GetLocalFile)
                            .Where(x => x?.ItemId != null && (x.MatchedBy == "manual" || (!resource.RetryRequested && x.Status == "auto")))
                            .OrderByDescending(x => x!.MatchedBy == "manual").FirstOrDefault();
                        if (preserved != null) store.SetFileLink(resource.Path, preserved.ItemId, preserved.Confidence, preserved.MatchedBy, "auto", true);
                        else store.SetFileLink(resource.Path, null, null, null, "unknown", true);
                        resource.RecognitionState = preserved != null ? "bound" : resource.SearchCached ? (resource.Candidates.Count > 0 ? "review" : "unknown") : online ? "unknown" : "offline";
                        Save(resource, scanId);
                    }
                    if (resource.RecognitionState != "bound" && online && !resource.SearchCached)
                    {
                        foreach (var id in resource.Aliases.Prepend(resource.Path).SelectMany(p => Digits(Path.GetFileName(p))).Distinct())
                        {
                            ct.ThrowIfCancellationRequested();
                            if (!ids.TryGetValue(id, out var item))
                            {
                                try { item = await catalog.Add(id.ToString(), ct).ConfigureAwait(false); }
                                catch (BoothNotFoundException) { item = null; }
                                ids[id] = item;
                            }
                            if (item == null) continue;
                            lock (gate)
                            {
                                store.SetFileLink(resource.Path, id, 1, "id_remote", "auto", true);
                                resource.RecognitionState = "bound"; resource.Candidates = []; resource.SearchCached = false;
                            }
                            break;
                        }
                        if (resource.RecognitionState != "bound")
                        {
                            var query = CleanName(Path.GetFileName(resource.Path));
                            if (query.Length > 0)
                            {
                                if (!searches.TryGetValue(query, out var hits)) searches[query] = hits = (await catalog.Search(query, ct).ConfigureAwait(false)).Where(x => x.Id > 0).DistinctBy(x => x.Id).Take(10).ToList();
                                resource.Candidates = hits;
                            }
                            resource.SearchCached = true;
                            resource.RecognitionState = resource.Candidates.Count > 0 ? "review" : "unknown";
                        }
                    }
                    lock (gate)
                    {
                        // Confirmation can occur while a request is in flight; it always wins.
                        if (store.GetLocalFile(resource.Path)?.MatchedBy == "manual") { resource.RecognitionState = "bound"; resource.Candidates = []; }
                        else if (resource.RecognitionState != "bound") store.SetFileLink(resource.Path, null, null, null, resource.Candidates.Count > 0 ? "review" : "unknown", true);
                        resource.RetryRequested = false;
                        Save(resource, scanId);
                        if (resource.RecognitionState == "bound") progress.Bound++;
                        else if (resource.RecognitionState == "review") progress.Review++;
                        else progress.Unknown++;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lock (gate) { resource.RecognitionState = "error"; resource.Error = ex.Message; Save(resource, scanId); progress.Errors++; progress.Error = ex.Message; }
                }
                lock (gate) progress.Processed++;
            }
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                // Publish tombstones only after a complete, successful scan. Old local_files remain intact.
                if (progress.Errors == 0)
                {
                    // Physical scope, not the old root column: subroot scans never tombstone siblings.
                    foreach (var path in MetadataPaths().Where(p => Within(p, root)))
                        Execute("UPDATE resource_meta SET active=0 WHERE path=$p AND COALESCE(last_scan,'')<>$s", ("$p", path), ("$s", scanId));
                    Execute("INSERT INTO resource_structure_version(path,version) VALUES($p,2) ON CONFLICT(path) DO UPDATE SET version=2", ("$p", root));
                }
                progress.State = progress.Errors > 0 ? "completed_with_errors" : "completed"; progress.CurrentPath = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { lock (gate) progress.State = "cancelled"; }
        catch (Exception ex) { lock (gate) { progress.State = "failed"; progress.Error = ex.Message; } }
    }

    public object List(string? status, int page, int size, string? q = null)
    {
        lock (gate)
        {
            page = Math.Max(1, page); size = Math.Clamp(size, 1, 500);
            using var c = db.Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT m.path FROM resource_meta m JOIN local_files f ON f.path=m.path WHERE m.active=1";
            if (!string.IsNullOrWhiteSpace(status) && status != "all") { cmd.CommandText += " AND (f.status=$st OR m.recognition_state=$st)"; cmd.Parameters.AddWithValue("$st", status); }
            if (!string.IsNullOrWhiteSpace(q)) { cmd.CommandText += " AND (f.name LIKE $q OR m.category LIKE $q OR m.aliases_json LIKE $q OR EXISTS(SELECT 1 FROM items i WHERE i.id=f.item_id AND i.title LIKE $q))"; cmd.Parameters.AddWithValue("$q", "%" + q.Trim() + "%"); }
            cmd.CommandText += " ORDER BY m.category,f.name COLLATE NOCASE";
            using var r = cmd.ExecuteReader(); var paths = new List<string>(); while (r.Read()) paths.Add(r.GetString(0));
            return new { files = paths.Skip((page - 1) * size).Take(size).Select(Dto).ToList(), total = paths.Count };
        }
    }
    public ResourceFile? Get(string path) { lock (gate) return Dto(ResourcePaths.Full(path)); }
    public object Confirm(string path, long id)
    {
        lock (gate)
        {
            path = ResourcePaths.Full(path); var resource = Meta(path) ?? throw new KeyNotFoundException("Resource not found.");
            if (!resource.Candidates.Any(x => x.Id == id) && !store.Exists(id)) throw new ArgumentException("ID is neither a candidate nor an existing item.");
            if (!store.Exists(id)) throw new ArgumentException("Add candidate through Catalog.Add before confirming.");
            store.SetFileLink(path, id, 1, "manual", "auto"); resource.Candidates = []; resource.SearchCached = false; resource.RecognitionState = "bound"; resource.Error = null; Save(resource, null);
            return Dto(path)!;
        }
    }
    public ResourceFile Unbind(string path)
    {
        lock (gate)
        {
            if (worker is { IsCompleted: false }) throw new InvalidOperationException("请等待当前扫描结束后再清除绑定。");
            path = ResourcePaths.Full(path);
            var resource = Meta(path) ?? throw new KeyNotFoundException("Resource not found.");
            var previousId = store.GetLocalFile(path)?.ItemId;
            // Clear the container and its inherited links, not unrelated resources using the same item.
            foreach (var alias in resource.Aliases.Prepend(path).Distinct(ResourcePaths.Comparer))
                if (ResourcePaths.Comparer.Equals(alias, path) || store.GetLocalFile(alias)?.ItemId == previousId)
                    store.SetFileLink(alias, null, null, null, "unknown");
            foreach (var member in resource.Members)
            {
                var f = store.GetLocalFile(member.Path);
                if (f?.MatchedBy == "container" && f.ItemId == previousId)
                    store.SetFileLink(member.Path, null, null, null, "unknown");
            }
            // Cache an empty recognition result so a routine rescan cannot restore the wrong ID.
            // Explicit Retry clears this cache; manually choosing a new product also works immediately.
            resource.Candidates = []; resource.SearchCached = true; resource.RecognitionState = "unknown";
            resource.Error = null; resource.RetryRequested = false; resource.Skipped = false;
            Save(resource, null);
            return Dto(path)!;
        }
    }
    /// <summary>Mark an explicit refresh of members/local cover and non-manual recognition; manual links remain intact.</summary>
    public object Retry(string path)
    {
        lock (gate)
        {
            path = ResourcePaths.Full(path); var resource = Meta(path) ?? throw new KeyNotFoundException("Resource not found.");
            if (store.GetLocalFile(path)?.MatchedBy != "manual")
            {
                resource.Candidates = []; resource.SearchCached = false; resource.RecognitionState = "unknown";
            }
            // Manual binding is immutable here; Retry still explicitly refreshes its members/local cover.
            resource.Error = null; resource.RetryRequested = true; resource.Skipped = false;
            Save(resource, null); return Dto(path)!;
        }
    }
    public string PrepareImport(string containerPath, string? memberPath = null)
    {
        lock (gate)
        {
            containerPath = ResourcePaths.Full(containerPath); var resource = Meta(containerPath) ?? throw new KeyNotFoundException("Resource not found.");
            var local = store.GetLocalFile(containerPath);
            if (local?.ItemId is not long id || local.Status != "auto" || !store.Exists(id)) throw new ArgumentException("Confirm resource before import.");
            var source = string.IsNullOrWhiteSpace(memberPath) ? containerPath : ResourcePaths.Full(memberPath);
            if (!ResourcePaths.Comparer.Equals(source, containerPath) && !resource.Members.Any(x => ResourcePaths.Comparer.Equals(x.Path, source))) throw new ArgumentException("Source is not a registered container member.");
            ResourcePaths.NoLinks(source);
            if (!ResourcePaths.Comparer.Equals(source, containerPath))
            {
                var existing = store.GetLocalFile(source);
                if (existing?.MatchedBy == "manual" && existing.ItemId != id) throw new ArgumentException("Member has a different manual binding.");
                store.UpsertLocalFile(FileRecord(source, resource.Root)); store.SetFileLink(source, id, 1, "container", "auto", true);
                if (store.GetLocalFile(source)?.Status != "auto") throw new ArgumentException("Member is ignored; select a different source.");
            }
            return source;
        }
    }

    public object Repair(string root)
    {
        root = ResourcePaths.Full(root);
        lock (gate) return Start(root, Boundaries().GetValueOrDefault(root, "library"), false, rebuildStructure: true);
    }
    private sealed record RootRegistration(string Path, string Mode);
    private List<RootRegistration> RegisteredRoots()
    {
        using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT path,mode FROM resource_roots ORDER BY length(path),path";
        using var r = cmd.ExecuteReader(); var result = new List<RootRegistration>();
        while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1))); return result;
    }
    private List<string> MetadataPaths()
    {
        using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT path FROM resource_meta";
        using var r = cmd.ExecuteReader(); var result = new List<string>(); while (r.Read()) result.Add(r.GetString(0)); return result;
    }
    private static bool Within(string path, string root) => ResourcePaths.Comparer.Equals(path, root) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private string? RegisteredMode(string path) => RegisteredRoots().FirstOrDefault(x => ResourcePaths.Comparer.Equals(x.Path, path))?.Mode;
    private string CatalogRoot(string scope)
    {
        // Include old metadata roots to migrate databases created before root registration existed.
        var roots = RegisteredRoots().Select(x => x.Path).ToList();
        using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT DISTINCT root FROM resource_meta";
        using (var r = cmd.ExecuteReader()) while (r.Read()) if (!r.IsDBNull(0)) roots.Add(r.GetString(0));
        return roots.Where(x => !string.IsNullOrWhiteSpace(x) && Within(scope, x)).OrderBy(x => x.Length).FirstOrDefault() ?? scope;
    }
    private Dictionary<string, string> Boundaries()
    {
        using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT path,kind FROM folder_boundary";
        using var r = cmd.ExecuteReader(); var result = new Dictionary<string, string>(ResourcePaths.Comparer);
        while (r.Read()) result[r.GetString(0)] = r.GetString(1); return result;
    }
    public object ListRoots() { lock (gate) return new { roots = RegisteredRoots() }; }
    public object GetBoundaries(string? root = null)
    {
        lock (gate) { var scope = root == null ? null : ResourcePaths.Full(root); return new { boundaries = Boundaries().Where(x => scope == null || Within(x.Key, scope)).Select(x => new { path = x.Key, kind = x.Value }).ToList() }; }
    }
    public object Structure(string root)
    {
        root = ResourcePaths.Full(root); ResourcePaths.RequireDirectory(root); ResourcePaths.NoLinks(root);
        lock (gate)
        {
            var catalogRoot = CatalogRoot(root); var boundaries = Boundaries();
            var metadata = MetadataPaths().Where(p => Within(p, catalogRoot)).Select(Meta).OfType<Resource>().ToList();
            var folders = new List<object>(); var truncated = false;
            void Visit(string dir, int depth)
            {
                if (depth > 12 || folders.Count >= 2000) { truncated = true; return; }
                var relative = Path.GetRelativePath(catalogRoot, dir);
                var kind = boundaries.GetValueOrDefault(dir, "auto");
                var category = kind == "category" || (kind == "auto" && !Digits(Path.GetFileName(dir)).Any() &&
                    (Category(Path.GetFileName(dir)) || metadata.Any(x => !ResourcePaths.Comparer.Equals(x.Path, dir) && Within(x.Path, dir) && x.Category.Equals(relative, StringComparison.OrdinalIgnoreCase))));
                var effectiveKind = kind == "asset" ? "asset" : ResourcePaths.Comparer.Equals(dir, catalogRoot) && RegisteredMode(dir) == "library" ? "category" : category ? "category" : "asset";
                folders.Add(new { path = dir, name = Path.GetFileName(dir), kind, effectiveKind, resourceCount = metadata.Count(x => Within(x.Path, dir)), depth });
                if (depth > 0 && effectiveKind != "category") return;
                foreach (var child in Directory.EnumerateDirectories(dir).OrderBy(x => x, ResourcePaths.Comparer))
                {
                    if (folders.Count >= 2000) { truncated = true; break; }
                    var name = Path.GetFileName(child);
                    if (name.StartsWith('.') || Skip.Contains(name) || (File.GetAttributes(child) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
                    Visit(child, depth + 1);
                }
            }
            Visit(root, 0); return new { root, catalogRoot, folders, truncated, limit = 2000 };
        }
    }
    public object ConfigureBoundary(string path, string kind)
    {
        path = ResourcePaths.Full(path); kind = kind.Trim().ToLowerInvariant();
        if (kind is not ("category" or "asset" or "auto")) throw new ArgumentException("Boundary kind: category/asset/auto.");
        ResourcePaths.RequireDirectory(path); ResourcePaths.NoLinks(path);
        lock (gate)
        {
            if (!RegisteredRoots().Any(x => Within(path, x.Path))) throw new ArgumentException("Folder must be within a registered library root.");
            if (worker is { IsCompleted: false }) throw new InvalidOperationException("Wait for the current scan before changing boundaries.");
            if (kind == "auto") Execute("DELETE FROM folder_boundary WHERE path=$p", ("$p", path));
            else Execute("INSERT INTO folder_boundary(path,kind) VALUES($p,$k) ON CONFLICT(path) DO UPDATE SET kind=excluded.kind", ("$p", path), ("$k", kind));
            return new { path, kind };
        }
    }

    private static string Mode(string? mode)
    { mode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant(); return mode is "auto" or "library" or "category" or "asset" ? mode : throw new ArgumentException("Mode: auto/library/category/asset."); }
    private static bool Category(string name) => !Digits(name).Any() && (Categories.Contains(name) || Categories.Contains(Regex.Replace(name, @"^[\d\s_\-]+", "")) || Regex.IsMatch(name, @"^0+[^\d\s]"));
    private static string? Kind(string name) => Archives.FirstOrDefault(x => name.EndsWith(x, StringComparison.OrdinalIgnoreCase)) is { } ext ? ext == ".unitypackage" ? "unitypackage" : ext == ".zip" ? "zip" : "archive" : null;
    private static string Stem(string name) { var ext = Archives.FirstOrDefault(x => name.EndsWith(x, StringComparison.OrdinalIgnoreCase)); return ext == null ? name : name[..^ext.Length]; }
    private static IEnumerable<long> Digits(string name) => Regex.Matches(Stem(name), @"(?<!\d)\d{5,9}(?!\d)").Select(x => long.Parse(x.Value)).Distinct();
    private static string CleanName(string name)
    {
        var s = Stem(name).Normalize(NormalizationForm.FormKC);
        s = Regex.Replace(s, @"[\[【(（][^\]】)）]*(?:修订|修正|修改|更新|revision|revised|主要)[^\]】)）]*[\]】)）]", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(?i)(?:^|[\s_\-])v?\d+(?:\.\d+)+(?:[a-z]\d*)?", " ");
        s = Regex.Replace(s, @"(?<!\d)\d{5,9}(?!\d)", " ");
        s = Regex.Replace(s, @"^[\s_\-#\d]+", "");
        s = Regex.Replace(s, @"[\[\]【】()（）_]+", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '.');
        return s.Length > 120 ? s[..120] : s;
    }
    private static string GroupName(string name) => Regex.Replace(CleanName(name), @"[\s\p{P}\p{S}]+", "").ToLowerInvariant();
    private sealed record Discovery(List<Resource> Resources, string ResolvedMode, string Explanation, List<string> Warnings, bool NeedsMode);
    private Discovery Discover(string root, string mode, CancellationToken ct)
    {
        ResourcePaths.NoLinks(root);
        var catalogRoot = CatalogRoot(root);
        var boundaries = Boundaries();
        var history = MetadataPaths().Where(p => Within(p, catalogRoot)).Select(Meta).OfType<Resource>().ToList();
        var resources = new List<Resource>(); var warnings = new List<string>(); var visited = 0;
        var entriesCache = new Dictionary<string, List<string>>(ResourcePaths.Comparer);
        // Read persisted container boundaries before any structural probing. A resolved container is opaque.
        var cached = new Dictionary<string, Resource>(ResourcePaths.Comparer);
        lock (gate)
        {
            using var c = db.Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT path FROM resource_meta";
            var paths = new List<string>(); using (var reader = cmd.ExecuteReader()) while (reader.Read()) paths.Add(reader.GetString(0));
            foreach (var path in paths)
            {
                var old = Meta(path); var local = store.GetLocalFile(path);
                if (old == null || old.RetryRequested || !Within(path, catalogRoot)) continue;
                if (local?.ItemId != null || old.RecognitionState == "review" || old.SearchCached)
                {
                    old.Skipped = true;
                    if (local?.ItemId != null) old.RecognitionState = "bound";
                    cached[path] = old;
                }
            }
        }
        List<string> Children(string dir)
        {
            ct.ThrowIfCancellationRequested();
            // Boundary probes may inspect only the shallow entries of a stale category card.
            // Members() still treats valid cached asset containers as opaque.
            if (entriesCache.TryGetValue(dir, out var found)) return found;
            var entries = new List<string>();
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                ct.ThrowIfCancellationRequested(); if (++visited > 200000) throw new IOException("Container entry limit exceeded.");
                if (Path.GetFileName(path).StartsWith('.') || Skip.Contains(Path.GetFileName(path))) continue;
                if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
                entries.Add(path);
            }
            return entriesCache[dir] = entries;
        }
        bool Part(string path) => AssetParts.Contains(Path.GetFileName(path));
        bool DirectAsset(List<string> entries) => entries.Any(p => !Directory.Exists(p) && (Kind(Path.GetFileName(p)) != null || new[] { ".fbx", ".blend", ".obj", ".prefab" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)));
        bool Product(string path, int depth = 0)
        {
            if (cached.ContainsKey(path)) return true;
            if (!Directory.Exists(path)) return Kind(Path.GetFileName(path)) != null;
            if (Part(path) || depth > 4) return false;
            var entries = Children(path);
            if (DirectAsset(entries) || entries.Any(p => Directory.Exists(p) && Part(p))) return true;
            var dirs = entries.Where(Directory.Exists).ToList();
            // A unique wrapper chain belongs to its outer resource, never a synthetic category.
            return dirs.Count == 1 && Product(dirs[0], depth + 1);
        }
        bool IsCategory(string path, bool structural = false)
        {
            if (boundaries.TryGetValue(path, out var explicitKind)) return explicitKind == "category";
            var name = Path.GetFileName(path);
            if (Digits(name).Any()) return false;
            // Historical confirmed parents are assets even if an old scan split their nested branches.
            if (cached.TryGetValue(path, out var bound) && bound.RecognitionState == "bound") return false;
            var relative = Path.GetRelativePath(catalogRoot, path);
            if (history.Any(x => !ResourcePaths.Comparer.Equals(x.Path, path) && Within(x.Path, path) &&
                (x.Category.Equals(relative, StringComparison.OrdinalIgnoreCase) || x.Category.StartsWith(relative + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))) return true;
            if (Category(name)) return true; // Loose archives never disqualify a named category.
            if (!structural || Part(path) || cached.ContainsKey(path)) return false;
            var entries = Children(path); var dirs = entries.Where(Directory.Exists).ToList();
            if (dirs.Any(Part) || DirectAsset(entries)) return false;
            return dirs.Count >= 2 && dirs.All(p => !Part(p) && Product(p));
        }
        Resource Create(string path, string category)
        {
            if (cached.TryGetValue(path, out var old)) { old.Root = catalogRoot; old.Category = category; return old; }
            Resource? previous; lock (gate) previous = Meta(path);
            return new() { Path = path, Root = catalogRoot, Category = category, RetryRequested = previous?.RetryRequested ?? false,
                LocalCoverPath = IsUploadedCover(previous?.LocalCoverPath) ? previous!.LocalCoverPath : null };
        }
        void Level(string dir, string category, bool allowCategories, int depth)
        {
            ct.ThrowIfCancellationRequested(); if (depth > 12) throw new IOException("Category nesting exceeds 12.");
            var groups = new List<Resource>();
            foreach (var path in Children(dir).OrderByDescending(Directory.Exists).ThenBy(x => x, ResourcePaths.Comparer))
            {
                var folder = Directory.Exists(path); var name = Path.GetFileName(path);
                if (folder && allowCategories && IsCategory(path, depth == 0 && ResourcePaths.Comparer.Equals(dir, catalogRoot))) { Level(path, Path.GetRelativePath(catalogRoot, path), true, depth + 1); continue; }
                if (!folder && Kind(name) == null) continue;
                var key = GroupName(name); var digits = Digits(name).ToArray();
                var existing = groups.FirstOrDefault(x => (key.Length > 0 && GroupName(Path.GetFileName(x.Path)) == key) || digits.Intersect(Digits(Path.GetFileName(x.Path))).Any());
                if (existing != null)
                {
                    if (existing.Skipped) continue;
                    if (folder && !Directory.Exists(existing.Path)) { existing.Aliases.Add(existing.Path); existing.Path = path; }
                    else existing.Aliases.Add(path);
                }
                else groups.Add(Create(path, category));
            }
            resources.AddRange(groups);
        }
        var resolved = mode; var explanation = "使用显式指定的扫描模式。"; var needsMode = false;
        if (mode == "auto")
        {
            if (boundaries.TryGetValue(root, out var rootBoundary)) { resolved = rootBoundary; explanation = "使用持久化的显式目录边界。"; }
            else if (RegisteredMode(root) is { } registeredMode) { resolved = registeredMode; explanation = "使用已登记资源库的稳定根模式。"; }
            else if (Directory.Exists(root) && IsCategory(root)) { resolved = "category"; explanation = "根据分类边界与历史资源归属识别分类目录。"; }
            else if (cached.ContainsKey(root)) { resolved = "asset"; explanation = "复用已识别的根资源容器，跳过内部成员遍历。"; }
            else if (!Directory.Exists(root)) { resolved = "asset"; explanation = "选中的根路径是归档文件或 unitypackage，按单个资源处理。"; }
            else
            {
                var entries = Children(root); var dirs = entries.Where(Directory.Exists).ToList();
                var archives = entries.Where(p => !Directory.Exists(p) && Kind(Path.GetFileName(p)) != null).ToList();
                var productBranches = dirs.Count(p => !Part(p) && Product(p));
                var ownPackage = entries.Any(p => !Directory.Exists(p) && (Kind(Path.GetFileName(p)) == "unitypackage" || new[] { ".fbx", ".blend", ".obj", ".prefab" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)));
                if (dirs.Any(Part) || (ownPackage && productBranches < 2)) { resolved = "asset"; explanation = "根目录包含自身的资源包、模型或拆分材质贴图目录，整体保留为单个资源。"; }
                else if (dirs.Any(p => IsCategory(p, true))) { resolved = "library"; explanation = "根据重复产品分支或通用分类名称识别资源库；分类标签保留原始相对路径。"; }
                else if ((dirs.Count >= 2 && productBranches == dirs.Count) || (Category(Path.GetFileName(root)) && dirs.Count > 0) || (archives.Count > 0 && productBranches > 0) || (dirs.Count == 0 && archives.Count >= 2)) { resolved = "category"; explanation = "目录包含独立的产品分支或散装归档，按分类目录发现资源。"; }
                else
                {
                    resolved = "asset"; needsMode = true;
                    explanation = "目录结构存在歧义，保守地将当前目录及单一嵌套包装链整体视为一个资源。";
                    warnings.Add("目录层级尚待确认：当前保留为单个资源，请先预览，或显式选择 library/category/asset 以调整资源边界。");
                }
            }
        }
        if (resolved == "asset")
        {
            if (!Directory.Exists(root) && Kind(Path.GetFileName(root)) == null) throw new ArgumentException("Asset root must be a folder, archive or unitypackage.");
            var parent = Path.GetDirectoryName(root);
            var category = parent != null && Within(parent, catalogRoot) && !ResourcePaths.Comparer.Equals(parent, catalogRoot) ? Path.GetRelativePath(catalogRoot, parent) : "";
            resources.Add(Create(root, category));
        }
        else
        {
            ResourcePaths.RequireDirectory(root);
            var category = ResourcePaths.Comparer.Equals(root, catalogRoot) ? "" : Path.GetRelativePath(catalogRoot, root);
            Level(root, category, true, 0);
        }
        foreach (var resource in resources)
        {
            if (resource.Skipped) continue;
            var images = new List<(string Path, int Depth)>();
            void Members(string dir, int depth, bool coverAllowed)
            {
                if (cached.TryGetValue(dir, out var opaque))
                {
                    resource.Members.AddRange(opaque.Members);
                    if (coverAllowed && opaque.LocalCoverPath != null) images.Add((opaque.LocalCoverPath, depth));
                    return;
                }
                foreach (var path in Children(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Directory.Exists(path)) { if (depth < 5) Members(path, depth + 1, coverAllowed && !Part(path)); }
                    else
                    {
                        if (Kind(Path.GetFileName(path)) is { } kind) resource.Members.Add(new(path, Path.GetFileName(path), kind, new FileInfo(path).Length, depth));
                        if (coverAllowed && depth <= 4 && Images.Contains(Path.GetExtension(path))) images.Add((path, depth));
                    }
                }
            }
            foreach (var path in resource.Aliases.Prepend(resource.Path))
                if (Directory.Exists(path)) Members(path, 1, true);
                else if (Kind(Path.GetFileName(path)) is { } kind) resource.Members.Add(new(path, Path.GetFileName(path), kind, new FileInfo(path).Length, 0));
            resource.Members = resource.Members.DistinctBy(x => x.Path, ResourcePaths.Comparer).Take(10000).ToList();
            var packages = resource.Members.Where(x => x.Kind == "unitypackage").ToList();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GroupName(Path.GetFileName(resource.Path)) };
            if (packages.Count == 1) names.Add(GroupName(packages[0].Name));
            if (IsUploadedCover(resource.LocalCoverPath)) continue; // Manual uploads survive rescans and explicit retries.
            resource.LocalCoverPath = images.Where(x => names.Contains(GroupName(Path.GetFileNameWithoutExtension(x.Path))) || Regex.IsMatch(Path.GetFileName(x.Path), @"(?i)(cover|preview|thumbnail|商品|封面)") || !Regex.IsMatch(Path.GetFileNameWithoutExtension(x.Path), @"(?i)(?:body|face|hair|normal|mask|uv|scale|outfit[ _-]*guide|readme|说明|教程)"))
                .Select(x => new { x.Path, Score = (names.Contains(GroupName(Path.GetFileNameWithoutExtension(x.Path))) ? 100 : Regex.IsMatch(Path.GetFileName(x.Path), @"(?i)(cover|preview|thumbnail|商品|封面)") ? 80 : 10) - x.Depth })
                .OrderByDescending(x => x.Score).ThenBy(x => x.Path, ResourcePaths.Comparer).Select(x => x.Path).FirstOrDefault();
        }
        return new(resources, resolved, explanation, warnings, needsMode);
    }
    private string LocalCoverDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db.DbPath))!, "local-covers");
    private bool IsUploadedCover(string? path) => path != null && ResourcePaths.Comparer.Equals(Path.GetDirectoryName(path), LocalCoverDirectory);

    /// <summary>Replace a registered resource's fallback cover without changing any source assets or item covers.</summary>
    public ResourceFile SetLocalCover(string resourcePath, byte[] image, string? extension = null)
    {
        lock (gate)
        {
            resourcePath = ResourcePaths.Full(resourcePath);
            if (Dto(resourcePath) == null) throw new KeyNotFoundException("Resource not found.");
            if (image == null || image.Length == 0 || image.Length > 8 * 1024 * 1024) throw new ArgumentException("封面应为 1 字节至 8 MB 的图片。");
            var ext = DetectCover(image) ?? throw new ArgumentException("请选择 JPG、PNG、WebP 或 GIF 图片。");
            if (extension != null)
            {
                var supplied = "." + extension.Trim().TrimStart('.').ToLowerInvariant();
                if (supplied == ".jpeg") supplied = ".jpg";
                if (supplied != ext) throw new ArgumentException("图片扩展名与内容不一致。");
            }
            Directory.CreateDirectory(LocalCoverDirectory);
            ResourcePaths.NoLinks(LocalCoverDirectory);
            var destination = Path.Combine(LocalCoverDirectory, Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant() + ext);
            if (!File.Exists(destination))
            {
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllBytes(temporary, image); File.Move(temporary, destination, overwrite: true); }
                finally { File.Delete(temporary); }
            }
            ResourcePaths.NoLinks(destination);
            Execute("UPDATE resource_meta SET local_cover_path=$cover,updated_at=$now WHERE path=$p", ("$cover", destination), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$p", resourcePath));
            return Dto(resourcePath)!;
        }
    }

    public ResourceFile ClearLocalCover(string resourcePath)
    {
        lock (gate)
        {
            resourcePath = ResourcePaths.Full(resourcePath);
            if (Dto(resourcePath) == null) throw new KeyNotFoundException("Resource not found.");
            // Hash-addressed uploads may be shared by other resources. Keep their files and never delete source images.
            Execute("UPDATE resource_meta SET local_cover_path=NULL,updated_at=$now WHERE path=$p", ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$p", resourcePath));
            return Dto(resourcePath)!;
        }
    }

    private static string? DetectCover(ReadOnlySpan<byte> image)
    {
        if (image.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return ".jpg";
        if (image.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (image.StartsWith("GIF87a"u8) || image.StartsWith("GIF89a"u8)) return ".gif";
        if (image.Length >= 12 && image.StartsWith("RIFF"u8) && image[8..].StartsWith("WEBP"u8)) return ".webp";
        return null;
    }

    /// <summary>Return the registered, non-linked fallback image, including manual uploads. Source assets stay unchanged.</summary>
    public string? LocalCover(string path)
    {
        lock (gate)
        {
            var resource = Meta(ResourcePaths.Full(path)); var cover = resource?.LocalCoverPath;
            if (cover == null || !File.Exists(cover) || !Images.Contains(Path.GetExtension(cover))) return null;
            ResourcePaths.NoLinks(cover); return cover;
        }
    }
    private static LocalFile FileRecord(string path, string root) => new() { Path = path, RootPath = root, Name = Path.GetFileName(path), Kind = Directory.Exists(path) ? "folder" : Kind(Path.GetFileName(path)) ?? "other", Size = Directory.Exists(path) ? 0 : new FileInfo(path).Length, Mtime = File.GetLastWriteTimeUtc(path).ToString("O") };
    private void Execute(string sql, params (string Key, object? Value)[] values)
    { using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value); cmd.ExecuteNonQuery(); }
    private void Save(Resource r, string? scanId) => Execute("""
INSERT INTO resource_meta(path,root,category,aliases_json,members_json,candidates_json,recognition_state,error,active,last_scan,updated_at,local_cover_path,retry_requested,last_scan_skipped)
VALUES($p,$r,$c,$a,$m,$h,$s,$e,1,$scan,$now,$cover,$retry,$skipped)
ON CONFLICT(path) DO UPDATE SET root=excluded.root,category=excluded.category,aliases_json=excluded.aliases_json,
 members_json=excluded.members_json,candidates_json=excluded.candidates_json,recognition_state=excluded.recognition_state,
 error=excluded.error,active=1,last_scan=COALESCE(excluded.last_scan,resource_meta.last_scan),updated_at=excluded.updated_at,
 local_cover_path=excluded.local_cover_path,retry_requested=excluded.retry_requested,last_scan_skipped=excluded.last_scan_skipped
""", ("$p", r.Path), ("$r", r.Root), ("$c", r.Category), ("$a", JsonSerializer.Serialize(r.Aliases, Json)), ("$m", JsonSerializer.Serialize(r.Members, Json)), ("$h", r.SearchCached || r.Candidates.Count > 0 ? JsonSerializer.Serialize(r.Candidates, Json) : null), ("$s", r.RecognitionState), ("$e", r.Error), ("$scan", scanId), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$cover", r.LocalCoverPath), ("$retry", r.RetryRequested ? 1 : 0), ("$skipped", r.Skipped ? 1 : 0));
    private Resource? Meta(string path)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT * FROM resource_meta WHERE path=$p"; cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
        string S(string n) => r.IsDBNull(r.GetOrdinal(n)) ? "" : r.GetString(r.GetOrdinal(n));
        return new() { Path = path, Root = S("root"), Category = S("category"), Aliases = JsonSerializer.Deserialize<List<string>>(S("aliases_json"), Json) ?? [], Members = JsonSerializer.Deserialize<List<ResourceMember>>(S("members_json"), Json) ?? [], Candidates = JsonSerializer.Deserialize<List<SearchHit>>(S("candidates_json") is { Length: > 0 } h ? h : "[]", Json) ?? [], SearchCached = !r.IsDBNull(r.GetOrdinal("candidates_json")), RecognitionState = S("recognition_state"), Error = S("error") is { Length: > 0 } e ? e : null, LocalCoverPath = S("local_cover_path") is { Length: > 0 } cover ? cover : null, RetryRequested = r.GetInt64(r.GetOrdinal("retry_requested")) != 0, Skipped = r.GetInt64(r.GetOrdinal("last_scan_skipped")) != 0 };
    }
    private ResourceFile? Dto(string path)
    {
        var meta = Meta(path); var f = store.GetLocalFile(path); if (meta == null || f == null) return null;
        var item = f.ItemId is long id ? store.Get(id) : null;
        return new() { Path = f.Path, Root = meta.Root, RootPath = f.RootPath, Name = f.Name, Kind = f.Kind, Size = f.Size, Mtime = f.Mtime, ItemId = f.ItemId, Confidence = f.Confidence, MatchedBy = f.MatchedBy, Status = f.Status, Imported = f.Imported, UpdatedAt = f.UpdatedAt, Title = item?.Title, Thumb = item?.Thumb, ShopName = item?.ShopName, HasImage = item?.HasImage ?? false, Category = meta.Category, Aliases = meta.Aliases, Members = meta.Members, Candidates = meta.Candidates, RecognitionState = meta.RecognitionState, Error = meta.Error, LocalCoverPath = meta.LocalCoverPath, RetryRequested = meta.RetryRequested, Skipped = meta.Skipped, Item = item };
    }
    public sealed record ScanStatus
    {
        public string? ScanId { get; set; } public string State { get; set; } = "idle"; public string? Root { get; set; } public string? Mode { get; set; }
        public int Discovered { get; set; } public int Processed { get; set; } public int Bound { get; set; } public int Review { get; set; } public int Unknown { get; set; }
        public int Errors { get; set; } public string? CurrentPath { get; set; } public string? Error { get; set; }
        public int Skipped { get; set; } public string? ResolvedMode { get; set; } public string? DetectionExplanation { get; set; }
        public List<string> Warnings { get; set; } = []; public bool NeedsMode { get; set; }
    }
    public sealed record ResourceMember(string Path, string Name, string Kind, long Size, int Depth);
    public class Resource
    {
        public string Path { get; set; } = ""; public string Root { get; set; } = ""; public string Category { get; set; } = "";
        public List<string> Aliases { get; set; } = []; public List<ResourceMember> Members { get; set; } = []; public List<SearchHit> Candidates { get; set; } = [];
        [System.Text.Json.Serialization.JsonIgnore] public bool SearchCached { get; set; }
        public string RecognitionState { get; set; } = "unknown"; public string? Error { get; set; }
        public string? LocalCoverPath { get; set; } public bool HasLocalCover => LocalCoverPath != null;
        public bool Skipped { get; set; } public bool RetryRequested { get; set; }
    }
    public sealed class ResourceFile : Resource
    {
        public string? RootPath { get; set; } public string Name { get; set; } = ""; public string Kind { get; set; } = "folder"; public long Size { get; set; } public string? Mtime { get; set; }
        public long? ItemId { get; set; } public double? Confidence { get; set; } public string? MatchedBy { get; set; } public string Status { get; set; } = "unknown";
        public bool Imported { get; set; } public string? UpdatedAt { get; set; } public string? Title { get; set; } public string? Thumb { get; set; } public string? ShopName { get; set; }
        public bool HasImage { get; set; } public Item? Item { get; set; }
    }
}
