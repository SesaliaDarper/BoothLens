using System.Text.Json;
using BoothLens.Core;
using BoothLens.Core.Booth;
using BoothLens.Core.Data;
using BoothLens.Core.Library;
using BoothLens.Core.Models;
using BoothLens.Core.Util;

var work = Path.Combine(Path.GetTempPath(), "BoothLensImport-" + Guid.NewGuid().ToString("N"));
var root = Path.Combine(work, "library"); Directory.CreateDirectory(root);
var paths = new AppPaths(Path.Combine(work, "data"));
var db = new Db(paths.DbPath); db.EnsureSchema(); var store = new ItemStore(db);
using var booth = new BoothClient(new NoNetwork());
var library = new ContainerLibrary(db, store, new Catalog(booth, store, paths), booth);
var importer = new Importer(paths, store);
var project = Path.Combine(work, "project"); Directory.CreateDirectory(Path.Combine(project, "Assets"));
void Check(string name, bool value) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
void Reject(string name, Action action)
{
    try { action(); } catch (Exception ex) when (ex is ArgumentException or IOException or KeyNotFoundException) { Console.WriteLine("PASS " + name); return; }
    throw new Exception("Expected rejection: " + name);
}
void Sql(Db database, string sql, params (string Key, object Value)[] args)
{
    using var c = database.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
    foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value); cmd.ExecuteNonQuery();
}
string Scalar(Db database, string sql)
{
    using var c = database.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(cmd.ExecuteScalar())!;
}
async Task Scan()
{
    library.Start(root, "library", false);
    for (var i = 0; i < 1000; i++)
    {
        var s = library.GetStatus(); if (s.State != "running") { Check("offline scan completed", s.State == "completed"); return; }
        await Task.Delay(10);
    }
    throw new Exception("Scan timed out");
}
string Container(string name)
{
    var dir = Path.Combine(root, name); Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "bundle.unitypackage"), "fixture-package");
    File.WriteAllText(Path.Combine(dir, "keep.txt"), "source-preserved"); return dir;
}
var review = Container("Review local name"); var unknown = Container("Unknown local name");
var unbound = Container("Explicitly unbound"); var bound = Container("Bound local name");
await Scan();
store.Upsert(new Item { Id = 1234567, Title = "Existing bound product" });
store.Upsert(new Item { Id = 7654321, Title = "Other existing product" });
library.Confirm(bound, 1234567); library.Confirm(unbound, 1234567); library.Unbind(unbound);
Sql(db, "UPDATE resource_meta SET recognition_state='review',candidates_json=$c WHERE path=$p", ("$c", "[{\"id\":7654321,\"title\":\"Candidate only\"}]"), ("$p", review));
store.SetFileLink(review, null, null, null, "review");
Sql(db, "UPDATE resource_meta SET recognition_state='unknown',candidates_json='[]' WHERE path=$p", ("$p", unknown));
string Snapshot(string path) => JsonSerializer.Serialize(library.Get(path));
foreach (var resource in new[] { review, unknown, unbound })
{
    var before = Snapshot(resource); var localBefore = JsonSerializer.Serialize(store.GetLocalFile(resource));
    var prepared = library.PrepareImport(resource);
    var stage = importer.Import(prepared, "copy", null);
    Check("local container staged without product: " + Path.GetFileName(resource), stage.State == "staged" && stage.ItemId == null && stage.Title == Path.GetFileName(resource));
    Check("staged copy preserved contents", File.ReadAllText(Path.Combine(stage.TargetPath!, "keep.txt")) == "source-preserved");
    var copied = importer.Import(prepared, "copy", project);
    Check("local copy queued without fabricated ID", copied.State == "queued" && copied.ItemId == null && copied.TargetPath!.Contains("local_" + SafeName.Clean(Path.GetFileName(resource)) + "__job"));
    Check("local copy queue record exists", File.Exists(Path.Combine(project, "Library", "BoothLens", "queue", copied.Id + ".json")));
    var member = Path.Combine(resource, "bundle.unitypackage");
    var memberBefore = store.GetLocalFile(member);
    var selected = library.PrepareImport(resource, member);
    var stagedMember = importer.Import(selected, "unitypackage", null);
    var queuedMember = importer.Import(selected, "unitypackage", project);
    Check("local package stages and queues", stagedMember.State == "staged" && queuedMember.State == "queued" && queuedMember.ItemId == null && queuedMember.Title == "bundle.unitypackage");
    Check("new member remains unbound", memberBefore == null && store.GetLocalFile(member)?.ItemId == null);
    Check("candidate, recognition and container link unchanged", Snapshot(resource) == before && JsonSerializer.Serialize(store.GetLocalFile(resource)) == localBefore);
}
Check("jobs list includes local titles", store.ListJobs(100).All(j => !string.IsNullOrWhiteSpace(j.Title)) && store.Count() == 2);
var boundBefore = Snapshot(bound);
var boundSource = library.PrepareImport(bound, Path.Combine(bound, "bundle.unitypackage"));
var boundJob = importer.Import(boundSource, "unitypackage", project);
Check("bound member and import identity preserved", boundJob.State == "queued" && boundJob.ItemId == 1234567 && boundJob.Title == "Existing bound product" && store.GetLocalFile(boundSource)?.MatchedBy == "container");
var boundCopy = importer.Import(library.PrepareImport(bound), "copy", project);
Check("bound copy naming preserved", Path.GetFileName(boundCopy.TargetPath!) == SafeName.FolderName(1234567, "Existing bound product") + "__job" + boundCopy.Id && Snapshot(bound) == boundBefore);
var stale = Path.Combine(unknown, "bundle.unitypackage"); store.SetFileLink(stale, 7654321, 1, "container", "auto");
library.PrepareImport(unknown, stale);
Check("unbound container never inherits a stale member identity", store.GetLocalFile(stale)?.ItemId == null && importer.Import(stale, "unitypackage", null).ItemId == null);
store.SetFileLink(boundSource, 7654321, 1, "container", "auto"); library.PrepareImport(bound, boundSource);
Check("inherited member follows current bound container", store.GetLocalFile(boundSource)?.ItemId == 1234567);
var independent = Path.Combine(review, "bundle.unitypackage"); store.SetFileLink(independent, 7654321, .9, "manual", "auto");
library.PrepareImport(review, independent);
Check("independent correct member binding preserved", store.GetLocalFile(independent)?.ItemId == 7654321 && store.GetLocalFile(independent)?.MatchedBy == "manual");
store.SetFileLink(boundSource, 7654321, 1, "manual", "auto"); Reject("conflicting manual member still blocked", () => library.PrepareImport(bound, boundSource));
store.SetFileLink(stale, null, null, null, "ignored"); Reject("ignored member still blocked", () => library.PrepareImport(unknown, stale));
store.SetFileLink(unknown, null, null, null, "ignored"); Reject("ignored container still blocked", () => library.PrepareImport(unknown));
Reject("direct ignored import still blocked", () => importer.Import(unknown, "copy", null));
Reject("unknown container still blocked", () => library.PrepareImport(Path.Combine(root, "missing")));
Reject("unregistered import still blocked", () => importer.Import(Path.Combine(root, "missing"), "copy", null));
Reject("unlisted member still blocked", () => library.PrepareImport(review, Path.Combine(review, "keep.txt")));
var outside = Path.Combine(work, "outside.unitypackage"); File.WriteAllText(outside, "outside");
Sql(db, "UPDATE resource_meta SET members_json=$m WHERE path=$p", ("$m", JsonSerializer.Serialize(new[] { new ContainerLibrary.ResourceMember(outside, "outside", "unitypackage", 7, 1) })), ("$p", review));
Reject("even forged outside membership blocked", () => library.PrepareImport(review, outside));
store.UpsertLocalFile(new LocalFile { Path = outside, Name = "outside", RootPath = root });
Check("direct import root check retained", importer.Import(outside, "copy", null).State == "failed");
var broken = Path.Combine(root, "broken.unitypackage"); File.WriteAllText(broken, "broken");
store.UpsertLocalFile(new LocalFile { Path = broken, RootPath = root, Name = "Broken local source" });
store.SetFileLink(broken, 9999999, .5, "id_in_name", "auto");
Check("dangling item does not become fabricated import identity", importer.Import(broken, "unitypackage", null) is { State: "staged", ItemId: null, Title: "Broken local source" } && store.GetLocalFile(broken)?.ItemId == 9999999);
store.SetFileLink(broken, 7654321, .5, "title_fuzzy", "review");
var reviewLinkBefore = JsonSerializer.Serialize(store.GetLocalFile(broken));
Check("review candidate link not used as confirmed import identity", importer.Import(broken, "unitypackage", null).ItemId == null && JsonSerializer.Serialize(store.GetLocalFile(broken)) == reviewLinkBefore);
var linked = Path.Combine(root, "linked"); var linkCreated = false;
try { Directory.CreateSymbolicLink(linked, bound); linkCreated = true; }
catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { Console.WriteLine("SKIP symlink creation privilege unavailable"); }
if (linkCreated)
{
    store.UpsertLocalFile(new LocalFile { Path = linked, RootPath = root, Name = "linked", Kind = "folder" });
    Check("direct unbound link import blocked by NoLinks", importer.Import(linked, "copy", null).State == "failed");
    Directory.Delete(linked);
}
var fallbackId = store.CreateJob(null, Path.Combine(work, "not-registered.zip"), "copy");
Check("job path name fallback", store.GetJob(fallbackId)?.Title == "not-registered.zip");

// Isolated legacy database: nullable migration must not cascade-delete jobs or incoming FK rows.
var legacy = new Db(Path.Combine(work, "legacy", "db.sqlite")); legacy.EnsureSchema();
Sql(legacy, """
PRAGMA foreign_keys=ON;
DROP TABLE import_jobs;
CREATE TABLE import_jobs (
 id INTEGER PRIMARY KEY AUTOINCREMENT, item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
 local_path TEXT, mode TEXT NOT NULL DEFAULT 'copy', state TEXT NOT NULL DEFAULT 'queued', message TEXT,
 target_path TEXT, created_at TEXT NOT NULL DEFAULT '', finished_at TEXT, extra TEXT DEFAULT 'legacy');
CREATE INDEX ix_jobs_state ON import_jobs(state);
CREATE INDEX ix_jobs_legacy ON import_jobs(local_path);
CREATE TABLE job_receipts(job_id INTEGER REFERENCES import_jobs(id) ON DELETE CASCADE, content TEXT);
CREATE TABLE job_audit(job_id INTEGER);
CREATE TRIGGER job_insert_audit AFTER INSERT ON import_jobs BEGIN INSERT INTO job_audit VALUES(new.id); END;
INSERT INTO items(id,title) VALUES(1234567,'Legacy bound title');
INSERT INTO import_jobs(id,item_id,local_path,mode,state,message,target_path,created_at,finished_at,extra)
 VALUES(42,1234567,'legacy.unitypackage','unitypackage','done','keep-message','keep-target','created','finished','keep-extra');
INSERT INTO job_receipts VALUES(42,'keep-receipt');
INSERT INTO import_jobs(id,item_id) VALUES(100,1234567);
DELETE FROM import_jobs WHERE id=100;
""");
legacy.EnsureSchema(); legacy.EnsureSchema();
var legacyStore = new ItemStore(legacy); var preserved = legacyStore.GetJob(42)!;
Check("legacy bound jobs and all fields preserved", preserved.ItemId == 1234567 && preserved.State == "done" && preserved.Message == "keep-message" && preserved.TargetPath == "keep-target" && preserved.CreatedAt == "created" && preserved.FinishedAt == "finished" && Scalar(legacy, "SELECT extra FROM import_jobs WHERE id=42") == "keep-extra");
Check("incoming FK rows preserved", Scalar(legacy, "SELECT content FROM job_receipts WHERE job_id=42") == "keep-receipt");
Check("legacy FK definition retained", Scalar(legacy, "SELECT count(*) FROM pragma_foreign_key_list('import_jobs') WHERE \"table\"='items'") == "1");
var newJob = legacyStore.CreateJob(null, "new-local.zip", "copy");
Check("legacy schema accepts null and preserves autoincrement", newJob > 100 && legacyStore.GetJob(newJob)?.ItemId == null);
Check("legacy indexes and triggers retained", Scalar(legacy, "SELECT count(*) FROM sqlite_master WHERE name='ix_jobs_legacy'") == "1" && Scalar(legacy, "SELECT count(*) FROM job_audit WHERE job_id=" + newJob) == "1");
Check("legacy foreign key integrity", Scalar(legacy, "SELECT count(*) FROM pragma_foreign_key_check") == "0");
Check("all tests offline and no custom items", booth.RequestCount == 0 && store.Count() == 2);
Console.WriteLine("IMPORT CHECKS PASSED " + work);

sealed class NoNetwork : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new Exception("Unexpected network request");
}
