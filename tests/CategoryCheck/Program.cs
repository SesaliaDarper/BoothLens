using System.Text.Json;
using BoothLens.Core;
using BoothLens.Core.Booth;
using BoothLens.Core.Data;
using BoothLens.Core.Library;
using BoothLens.Core.Models;

var work = Path.Combine(Path.GetTempPath(), "BoothLensCategory-" + Guid.NewGuid().ToString("N"));
var rootA = Path.Combine(work, "library-a"); var rootB = Path.Combine(work, "library-b");
string Asset(string root, params string[] parts)
{
    var dir = parts.Aggregate(root, Path.Combine); Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "bundle.unitypackage"), "fixture"); return dir;
}
var direct = Asset(rootA, "clothes", "Dress");
var child = Asset(rootA, "clothes", "hair", "Ribbon");
var prefixSibling = Asset(rootA, "clothes-extra", "Coat"); Asset(rootA, "clothes-extra", "Jacket");
var otherRoot = Asset(rootB, "clothes", "Dress");
var nestedOnly = Asset(rootB, "models", "hair", "Wig");
var data = new AppPaths(Path.Combine(work, "data")); var db = new Db(data.DbPath); db.EnsureSchema();
var store = new ItemStore(db); using var booth = new BoothClient(new NoNetwork());
var library = new ContainerLibrary(db, store, new Catalog(booth, store, data), booth);
JsonElement J(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
void Check(string name, bool value) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
void Sql(string sql, params (string Key, object Value)[] args)
{
    using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
    foreach (var a in args) cmd.Parameters.AddWithValue(a.Key, a.Value); cmd.ExecuteNonQuery();
}
string Scalar(string sql, params (string Key, object Value)[] args)
{
    using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
    foreach (var a in args) cmd.Parameters.AddWithValue(a.Key, a.Value); return Convert.ToString(cmd.ExecuteScalar())!;
}
string Snapshot() => Scalar("SELECT group_concat(snapshot,'|') FROM (SELECT path||':'||COALESCE(item_id,'')||':'||COALESCE(matched_by,'')||':'||status||':'||imported AS snapshot FROM local_files ORDER BY path)")
    + "#" + Scalar("SELECT group_concat(snapshot,'|') FROM (SELECT path||':'||category||':'||aliases_json||':'||members_json||':'||COALESCE(candidates_json,'')||':'||recognition_state AS snapshot FROM resource_meta ORDER BY path)");
async Task Scan(string root, string mode = "library", bool repair = false)
{
    if (repair) library.Repair(root); else library.Start(root, mode, false);
    for (var n = 0; n < 1000; n++)
    {
        var s = library.GetStatus();
        if (s.State != "running") { Check("offline scan completed", s.State == "completed"); return; }
        await Task.Delay(10);
    }
    throw new Exception("Timeout");
}
JsonElement[] Categories(string? root = null) => J(library.ListCategories(root)).GetProperty("categories").EnumerateArray().ToArray();
JsonElement Category(string root, string category) => Categories(root).Single(x => x.GetProperty("category").GetString() == category);
void Reject(string name, Action action)
{
    try { action(); } catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException) { Check(name, true); return; }
    throw new Exception("Expected invalid category: " + name);
}

await Scan(rootA); await Scan(rootB);
Check("raw nested category inferred", library.Get(child)!.Category.Replace('\\', '/') == "clothes/hair");
Check("same category in independent roots", Categories().Count(x => x.GetProperty("category").GetString() == "clothes") == 2);
Check("implicit parent with no direct resources is listed", Category(rootB, "models").GetProperty("count").GetInt64() == 0);
library.RemoveCategory(rootB, "models");
Check("implicit parent removes nested category", library.Get(nestedOnly)!.Category == "" && Category(rootB, "models/hair").GetProperty("effectiveRemoved").GetBoolean());
library.RestoreCategory(rootB, "models");
Check("implicit parent restoration is immediate", library.Get(nestedOnly)!.Category.Replace('\\', '/') == "models/hair");
store.Upsert(new Item { Id = 1234567, Title = "Manual fixture" }); library.Confirm(direct, 1234567);
library.PrepareImport(direct, Path.Combine(direct, "bundle.unitypackage"));
Sql("UPDATE resource_meta SET candidates_json='[{\"id\":7654321,\"title\":\"cached\"}]',recognition_state='review' WHERE path=$p", ("$p", child));
var before = Snapshot();
var removed = library.RemoveCategory(rootA, "clothes");
Check("explicit removal state", removed.Removed && removed.EffectiveRemoved && removed.Count == 1);
Check("index-only removal preserves all raw metadata and links", Snapshot() == before);
Check("root-scoped category and descendants become uncategorized", library.Get(direct)!.Category == "" && library.Get(child)!.Category == "" && library.Get(otherRoot)!.Category == "clothes");
Check("prefix siblings are unaffected", library.Get(prefixSibling)!.Category == "clothes-extra");
Check("descendant reports inherited effective removal", !Category(rootA, "clothes/hair").GetProperty("removed").GetBoolean() && Category(rootA, "clothes/hair").GetProperty("effectiveRemoved").GetBoolean());
Check("List projection applies removal", J(library.List(null, 1, 500)).GetProperty("files").EnumerateArray().Where(x => x.GetProperty("root").GetString() == rootA && (x.GetProperty("path").GetString() == direct || x.GetProperty("path").GetString() == child)).All(x => x.GetProperty("category").GetString() == ""));
Check("Preview projection applies removal", J(library.Preview(rootA, "library")).GetProperty("files").EnumerateArray().Where(x => x.GetProperty("path").GetString() == child).All(x => x.GetProperty("category").GetString() == ""));
library.RemoveCategory(rootA, "clothes\\hair");
var restoredChild = library.RestoreCategory(rootA, "clothes/hair");
Check("restoring child under removed parent stays effectively removed", !restoredChild.Removed && restoredChild.EffectiveRemoved && library.Get(child)!.Category == "");
library.RemoveCategory(rootA, "CLOTHES");
Check("case-insensitive idempotent rule", Scalar("SELECT COUNT(*) FROM resource_category_rules") == "1");
var restore = library.RestoreCategory(rootA, "clothes");
Check("restore immediate without scan", !restore.Removed && !restore.EffectiveRemoved && library.Get(direct)!.Category == "clothes" && library.Get(child)!.Category.Replace('\\', '/') == "clothes/hair");
Check("restore preserves binding candidates and members", Snapshot() == before && library.Get(direct)!.ItemId == 1234567 && library.Get(child)!.Candidates.Count == 1);
library.RemoveCategory(rootA, "clothes");
await Scan(rootA); await Scan(Path.Combine(rootA, "clothes"), "auto"); await Scan(rootA, repair: true);
Check("ordinary subroot and repair scans never revive removed category", library.Get(direct)!.Category == "" && library.Get(child)!.Category == "");
Check("raw category remains persisted across scans", Scalar("SELECT category FROM resource_meta WHERE path=$p", ("$p", direct)) == "clothes");
Check("bindings candidates and cached members survive scans", library.Get(direct)!.ItemId == 1234567 && library.Get(child)!.Candidates.Count == 1 && library.Get(direct)!.Members.Count == 1 && store.GetLocalFile(Path.Combine(direct, "bundle.unitypackage"))!.MatchedBy == "container");
library = new ContainerLibrary(db, store, new Catalog(booth, store, data), booth);
Check("rules survive new library instance", library.Get(direct)!.Category == "");
library.RestoreCategory(rootA, "clothes");
Check("restoration after reload needs no scan", library.Get(child)!.Category.Replace('\\', '/') == "clothes/hair");
var rulesBeforeInvalid = Scalar("SELECT COUNT(*) FROM resource_category_rules");
foreach (var invalid in new[] { "", " ", "..", "clothes/../hair", "clothes/./hair", "/clothes", "\\clothes", "C:\\clothes", "//server/share", "clothes//hair", "clothes/", "clothes\0", "clothes?", "clothes. " })
    Reject("reject invalid relative category " + JsonSerializer.Serialize(invalid), () => library.RemoveCategory(rootA, invalid));
Reject("unknown category rejected", () => library.RemoveCategory(rootA, "missing"));
Reject("unregistered root rejected", () => library.RemoveCategory(work, "clothes"));
Reject("unregistered GET root rejected", () => library.ListCategories(work));
Check("invalid requests leave rules untouched", Scalar("SELECT COUNT(*) FROM resource_category_rules") == rulesBeforeInvalid);
Sql("INSERT INTO resource_meta(path,root,category,active) VALUES($p,$r,'historical-only',0)", ("$p", Path.Combine(rootA, "historical")), ("$r", rootA));
Check("inactive history is not listed", Categories(rootA).All(x => x.GetProperty("category").GetString() != "historical-only"));
library.RemoveCategory(rootA, "clothes");
Sql("UPDATE resource_meta SET active=0 WHERE root=$r AND category='clothes'", ("$r", rootA));
Check("rule-only category remains listed at zero count", Category(rootA, "clothes").GetProperty("count").GetInt64() == 0 && Category(rootA, "clothes").GetProperty("removed").GetBoolean());
Check("rule-only category can be restored", !library.RestoreCategory(rootA, "clothes").Removed);
Check("source files remain untouched", File.ReadAllText(Path.Combine(direct, "bundle.unitypackage")) == "fixture" && Directory.Exists(Path.Combine(rootA, "clothes")));
var parentRoot = Path.Combine(work, "parent"); var nestedRoot = Path.Combine(parentRoot, "nested");
var nestedAsset = Asset(nestedRoot, "clothes", "NestedDress");
await Scan(nestedRoot); library.RemoveCategory(nestedRoot, "clothes");
library.ConfigureBoundary(nestedRoot, "category"); await Scan(parentRoot);
Check("ancestor scan reparents removal rules without revival", library.Get(nestedAsset)!.Root == parentRoot && library.Get(nestedAsset)!.Category == "" && Category(parentRoot, "nested/clothes").GetProperty("removed").GetBoolean());
library.RestoreCategory(parentRoot, "nested/clothes");
Check("reparented rule restores immediately", library.Get(nestedAsset)!.Category.Replace('\\', '/') == "nested/clothes");
Check("no network", booth.RequestCount == 0 && booth.ImageRequestCount == 0);
Console.WriteLine("CATEGORY CHECKS PASSED " + work);
sealed class NoNetwork : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new Exception("Unexpected network request");
}
