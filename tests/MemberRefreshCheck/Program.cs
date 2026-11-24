using System.Text.Json;
using BoothLens.Core;
using BoothLens.Core.Booth;
using BoothLens.Core.Data;
using BoothLens.Core.Library;
using BoothLens.Core.Models;

var work = Path.Combine(Path.GetTempPath(), "BoothLensMembers-" + Guid.NewGuid().ToString("N"));
var root = Path.Combine(work, "library"); Directory.CreateDirectory(root);
var paths = new AppPaths(Path.Combine(work, "data"));
var db = new Db(paths.DbPath); db.EnsureSchema(); var store = new ItemStore(db);
using var booth = new BoothClient(new NoNetwork());
var library = new ContainerLibrary(db, store, new Catalog(booth, store, paths), booth);
void Check(string name, bool value) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
void Reject(string name, Action action)
{
    try { action(); } catch (Exception ex) when (ex is ArgumentException or IOException or KeyNotFoundException) { Console.WriteLine("PASS " + name); return; }
    throw new Exception("Expected rejection: " + name);
}
void Sql(string sql, params (string Key, object Value)[] args)
{
    using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
    foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value); cmd.ExecuteNonQuery();
}
string Snapshot(string table, params string[] except)
{
    using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT * FROM " + table + " ORDER BY 1";
    using var reader = cmd.ExecuteReader(); var rows = new List<Dictionary<string, object?>>();
    while (reader.Read())
    {
        var row = new Dictionary<string, object?>();
        for (var i = 0; i < reader.FieldCount; i++) if (!except.Contains(reader.GetName(i))) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        rows.Add(row);
    }
    return JsonSerializer.Serialize(rows);
}
string Package(string dir, string name)
{
    Directory.CreateDirectory(dir); var path = Path.Combine(dir, name); File.WriteAllText(path, "fixture:" + name); return path;
}
string Register(string name, string? directory = null)
{
    var path = directory ?? Path.Combine(root, name); Directory.CreateDirectory(path);
    store.UpsertLocalFile(new LocalFile { Path = path, RootPath = root, Name = name, Kind = "folder" });
    Sql("INSERT INTO resource_meta(path,root,category,members_json,candidates_json,recognition_state,error,active,last_scan,updated_at,retry_requested,last_scan_skipped) VALUES($p,$r,'clothes','[]','[]','unknown','keep-error',1,'keep-scan','old',1,1)", ("$p", path), ("$r", root));
    return path;
}
string Unchanged() => string.Join("\n", new[] { Snapshot("resource_meta", "members_json", "updated_at"), Snapshot("local_files"), Snapshot("items"), Snapshot("user_links"), Snapshot("resource_roots"), Snapshot("folder_boundary") });
var review = Register("Review cached empty");
var unknown = Register("Unknown cached empty");
var bound = Register("Bound cached empty");
store.Upsert(new Item { Id = 1234567, Title = "Existing product", Thumb = "keep-thumb" });
store.SetFileLink(bound, 1234567, .95, "manual", "auto"); store.AddUserLink(bound, 1234567, "keep-link");
Sql("UPDATE resource_meta SET candidates_json='[{\"id\":7654321,\"title\":\"Candidate only\"}]',recognition_state='review' WHERE path=$p", ("$p", review));
Sql("UPDATE resource_meta SET recognition_state='bound' WHERE path=$p", ("$p", bound));
var cover = Package(review, "cover.png");
Sql("UPDATE resource_meta SET local_cover_path=$c WHERE path=$p", ("$c", cover), ("$p", review));
var deepDir = review; for (var i = 0; i < 6; i++) deepDir = Path.Combine(deepDir, "wrapper" + i);
var deep = Package(deepDir, "deep.unitypackage");
var direct = Package(review, "local.zip"); var archive = Package(review, "data.tar.gz");
var alias = Package(root, "Review cached empty.7z");
Sql("UPDATE resource_meta SET aliases_json=$a WHERE path=$p", ("$a", JsonSerializer.Serialize(new[] { alias, alias, review })), ("$p", review));
foreach (var ignored in new[] { "Library", ".git", "#cache", "Temp", "__MACOSX" }) Package(Path.Combine(review, ignored), "skip.unitypackage");
Package(review, "notes.txt"); Package(unknown, "new.unitypackage"); Package(bound, "bound.unitypackage");
var stable = Unchanged();
var result = library.RefreshMembers(review);
Check("cached empty review gets deep6 and alias packages", result.Members.Count == 4 && result.Members.Any(m => m.Path == deep && m.Depth == 7) && result.Members.Any(m => m.Path == alias && m.Depth == 0));
Check("existing archive Kind semantics preserved", result.Members.Single(m => m.Path == direct).Kind == "zip" && result.Members.Single(m => m.Path == archive).Kind == "archive" && result.Members.Single(m => m.Path == deep).Kind == "unitypackage");
Check("only package kinds included; dot hash Skip trees excluded", result.Members.All(m => !m.Name.StartsWith("skip") && m.Path != cover));
Check("only members_json and updated_at change across every row", Unchanged() == stable);
var cache = Snapshot("resource_meta", "updated_at"); library.RefreshMembers(review);
Check("duplicate aliases and repeat refresh do not grow cache", Snapshot("resource_meta", "updated_at") == cache && library.Get(review)!.Members.Count == 4);
Check("unknown and bound containers refresh without recognition writes", library.RefreshMembers(unknown).Members.Count == 1 && library.RefreshMembers(bound).Members.Count == 1 && Unchanged() == stable);
Check("candidate bindings cover retry category links unchanged", library.Get(review) is { RecognitionState: "review", RetryRequested: true, Skipped: true, Category: "clothes" } r && r.Candidates.Single().Id == 7654321 && r.LocalCoverPath == cover && library.Get(bound)?.ItemId == 1234567 && store.GetLocalFile(deep) == null);
Sql("CREATE TRIGGER member_refresh_only_columns BEFORE UPDATE OF path,root,category,aliases_json,candidates_json,recognition_state,error,active,last_scan,local_cover_path,retry_requested,last_scan_skipped ON resource_meta BEGIN SELECT RAISE(ABORT,'non-member column update'); END;");
library.RefreshMembers(review); Check("SQL update targets only the two permitted columns", true);
Sql("DROP TRIGGER member_refresh_only_columns");
Reject("unknown path rejected", () => library.RefreshMembers(Path.Combine(root, "not-registered")));
Sql("UPDATE resource_meta SET active=0 WHERE path=$p", ("$p", unknown));
Reject("inactive rejected", () => library.RefreshMembers(unknown));
Sql("UPDATE resource_meta SET active=1 WHERE path=$p", ("$p", unknown));
store.SetFileLink(unknown, null, null, null, "ignored");
Reject("ignored container rejected", () => library.RefreshMembers(unknown));
store.SetFileLink(unknown, null, null, null, "unknown");
Directory.Delete(unknown, true); Reject("missing registered container rejected", () => library.RefreshMembers(unknown));
Directory.CreateDirectory(unknown);
var outside = Package(Path.Combine(work, "outside"), "external.zip");
Sql("UPDATE resource_meta SET aliases_json=$a WHERE path=$p", ("$a", JsonSerializer.Serialize(new[] { outside })), ("$p", unknown));
var beforeReject = Snapshot("resource_meta");
Reject("alias outside root rejected", () => library.RefreshMembers(unknown));
Check("failed refresh publishes nothing", Snapshot("resource_meta") == beforeReject);
Sql("UPDATE resource_meta SET aliases_json='[]',root=$r WHERE path=$p", ("$r", Path.Combine(root, "sibling")), ("$p", unknown));
Directory.CreateDirectory(Path.Combine(root, "sibling")); Reject("canonical container outside root rejected", () => library.RefreshMembers(unknown));
Sql("UPDATE resource_meta SET root=$r WHERE path=$p", ("$r", root), ("$p", unknown));
var aliasFolder = Path.Combine(root, "alias-folder"); var aliasPackage = Package(aliasFolder, "alias.unitypackage");
Sql("UPDATE resource_meta SET aliases_json=$a WHERE path=$p", ("$a", JsonSerializer.Serialize(new[] { aliasFolder })), ("$p", unknown));
Check("folder alias enumerates inside registered root", library.RefreshMembers(unknown).Members.Single().Path == aliasPackage);
var standalone = Package(root, "standalone.rar");
store.UpsertLocalFile(new LocalFile { Path = standalone, RootPath = root, Name = "standalone.rar", Kind = "archive" });
Sql("INSERT INTO resource_meta(path,root) VALUES($p,$r)", ("$p", standalone), ("$r", root));
Check("archive canonical container included at depth zero", library.RefreshMembers(standalone).Members.Single() is { Kind: "archive", Depth: 0 });
var bounded = Register("Bounded deep tree"); var atLimit = bounded;
for (var i = 0; i < 19; i++) atLimit = Path.Combine(atLimit, "depth" + i);
var edge = Package(atLimit, "edge.unitypackage"); Package(Path.Combine(atLimit, "too-deep"), "outside-depth.unitypackage");
Check("depth20 package included and depth21 excluded", library.RefreshMembers(bounded).Members.Single() is { Depth: 20 } atEdge && atEdge.Path == edge);
var missingAlias = Path.Combine(root, "missing-alias.zip");
Sql("UPDATE resource_meta SET aliases_json=$a WHERE path=$p", ("$a", JsonSerializer.Serialize(new[] { missingAlias })), ("$p", bounded));
Reject("missing alias rejected rather than publishing partial cache", () => library.RefreshMembers(bounded));
var excludedAlias = Package(Path.Combine(root, "Library"), "excluded.zip");
Sql("UPDATE resource_meta SET aliases_json=$a WHERE path=$p", ("$a", JsonSerializer.Serialize(new[] { excludedAlias })), ("$p", bounded));
Reject("explicit alias cannot enter excluded Library tree", () => library.RefreshMembers(bounded));

// NoLinks rejects explicit roots/aliases/members; no partial member cache is published.
var linkedMember = Path.Combine(review, "linked.unitypackage"); var linkCreated = false;
try { File.CreateSymbolicLink(linkedMember, outside); linkCreated = true; }
catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException || ex is IOException io && (io.HResult & 0xffff) == 1314) { Console.WriteLine("SKIP symlink creation privilege unavailable"); }
if (linkCreated)
{
    beforeReject = Snapshot("resource_meta"); Reject("linked member rejected", () => library.RefreshMembers(review));
    Check("linked-member rejection preserves cached members and all metadata", Snapshot("resource_meta") == beforeReject); File.Delete(linkedMember);
    File.CreateSymbolicLink(linkedMember, outside);
    Sql("UPDATE resource_meta SET aliases_json=$a WHERE path=$p", ("$a", JsonSerializer.Serialize(new[] { linkedMember })), ("$p", unknown));
    Reject("linked alias rejected", () => library.RefreshMembers(unknown)); File.Delete(linkedMember);
    var rootLink = Path.Combine(work, "root-link"); Directory.CreateSymbolicLink(rootLink, root);
    Sql("UPDATE resource_meta SET root=$r,aliases_json='[]' WHERE path=$p", ("$r", rootLink), ("$p", unknown));
    Reject("linked root rejected", () => library.RefreshMembers(unknown)); Directory.Delete(rootLink);
    var containerLink = Path.Combine(root, "container-link"); Directory.CreateSymbolicLink(containerLink, review);
    store.UpsertLocalFile(new LocalFile { Path = containerLink, RootPath = root, Name = "container-link", Kind = "folder" });
    Sql("INSERT INTO resource_meta(path,root) VALUES($p,$r)", ("$p", containerLink), ("$r", root));
    Reject("linked canonical container rejected", () => library.RefreshMembers(containerLink)); Directory.Delete(containerLink);
}
// Junctions on Windows do not require symlink privilege, so NoLinks is still exercised there.
if (OperatingSystem.IsWindows() && !linkCreated)
{
    void Junction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Junction creation failed: " + process.ExitCode);
    }
    var junction = Path.Combine(review, "linked-directory"); Junction(junction, Path.GetDirectoryName(outside)!);
    beforeReject = Snapshot("resource_meta"); Reject("junction member directory rejected", () => library.RefreshMembers(review));
    Check("junction rejection never publishes partial members", Snapshot("resource_meta") == beforeReject); Directory.Delete(junction);
    junction = Path.Combine(root, "alias-junction"); Junction(junction, aliasFolder);
    Sql("UPDATE resource_meta SET aliases_json=$a WHERE path=$p", ("$a", JsonSerializer.Serialize(new[] { junction })), ("$p", unknown));
    Reject("junction alias rejected", () => library.RefreshMembers(unknown)); Directory.Delete(junction);
    junction = Path.Combine(work, "root-junction"); Junction(junction, root);
    Sql("UPDATE resource_meta SET root=$r,aliases_json='[]' WHERE path=$p", ("$r", junction), ("$p", unknown));
    Reject("junction root rejected", () => library.RefreshMembers(unknown)); Directory.Delete(junction);
    junction = Path.Combine(root, "container-junction"); Junction(junction, review);
    store.UpsertLocalFile(new LocalFile { Path = junction, RootPath = root, Name = "container-junction", Kind = "folder" });
    Sql("INSERT INTO resource_meta(path,root) VALUES($p,$r)", ("$p", junction), ("$r", root));
    Reject("junction canonical container rejected", () => library.RefreshMembers(junction)); Directory.Delete(junction);
}
Check("local-only refresh never creates product or import jobs", booth.RequestCount == 0 && booth.ImageRequestCount == 0 && store.Count() == 1 && store.ListJobs().Count == 0);
Console.WriteLine("MEMBER REFRESH CHECKS PASSED " + work);

sealed class NoNetwork : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new Exception("Unexpected network request");
}
