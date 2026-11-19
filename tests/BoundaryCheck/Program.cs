using System.Net;
using System.Text.Json;
using BoothLens.Core;
using BoothLens.Core.Booth;
using BoothLens.Core.Data;
using BoothLens.Core.Library;
using BoothLens.Core.Models;

var work = Path.Combine(Path.GetTempPath(), "BoothLensBoundary-" + Guid.NewGuid().ToString("N"));
var root = Path.Combine(work, "library");
string Dir(params string[] parts) { var p = parts.Aggregate(root, Path.Combine); Directory.CreateDirectory(p); return p; }
void Package(string dir, string name = "bundle.unitypackage") => File.WriteAllText(Path.Combine(dir, name), "fixture");
var actors = Dir("0角色"); var clothes = Dir("0衣服");
var salt = Dir("0角色", "Salt"); Package(salt);
var kaguya = Dir("0角色", "Kaguya8562330");
Package(Dir("0角色", "Kaguya8562330", "Other"));
Package(Dir("0角色", "Kaguya8562330", "旧版本"));
var dress = Dir("0衣服", "Dress"); Package(dress);
Package(actors, "loose.zip"); Package(clothes, "other.7z");
var mixed = Dir("Mixed"); Package(Dir("Mixed", "Alpha")); Package(Dir("Mixed", "Beta"));
var data = new AppPaths(Path.Combine(work, "data")); var db = new Db(data.DbPath); db.EnsureSchema();
var store = new ItemStore(db); using var booth = new BoothClient(new NoNetwork());
var library = new ContainerLibrary(db, store, new Catalog(booth, store, data), booth);
JsonElement J(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
void Check(string name, bool value) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
void Sql(string sql, params (string Key, object Value)[] args) { using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var a in args) cmd.Parameters.AddWithValue(a.Key, a.Value); cmd.ExecuteNonQuery(); }
string Scalar(string sql) { using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(cmd.ExecuteScalar())!; }
async Task Wait() { for (var n = 0; n < 1000; n++) { var s = library.GetStatus(); if (s.State != "running") { Check("scan completed", s.State == "completed"); return; } await Task.Delay(10); } throw new Exception("Timeout"); }
List<ContainerLibrary.ResourceFile> Files() => J(library.List(null, 1, 500)).GetProperty("files").EnumerateArray().Select(x => library.Get(x.GetProperty("path").GetString()!)!).ToList();
var rootsBefore = Scalar("SELECT count(*) FROM resource_roots");
var preview = J(library.Preview(root, "library"));
Check("preview readonly registration", Scalar("SELECT count(*) FROM resource_roots") == rootsBefore);
Check("mixed named category with loose packages", preview.GetProperty("files").EnumerateArray().Any(x => x.GetProperty("path").GetString() == salt && x.GetProperty("category").GetString() == "0角色"));
library.Start(root, "library", false); await Wait();
Check("Kaguya multiple branches remain one product", Files().Count(x => x.Path.StartsWith(kaguya, StringComparison.OrdinalIgnoreCase)) == 1);
Check("mixed structural library category", Files().Any(x => x.Category == "Mixed"));
var total = Files().Count;
library.Start(actors, "auto", false); await Wait();
Check("subroot retains siblings and category", Files().Count == total && library.Get(dress) != null && library.Get(salt)!.Category == "0角色" && library.Get(salt)!.Root == root);
store.Upsert(new Item { Id = 1234567, Title = "Manual" });
library.Confirm(salt, 1234567); library.Confirm(kaguya, 1234567);
// Reproduce a bad migration: categories became review cards and children were tombstoned.
foreach (var p in new[] { actors, clothes }) {
 store.UpsertLocalFile(new LocalFile { Path = p, RootPath = root, Name = Path.GetFileName(p), Kind = "folder" });
 Sql("INSERT INTO resource_meta(path,root,category,candidates_json,recognition_state,active) VALUES($p,$r,'','[{\"id\":7654321,\"title\":\"candidate\"}]','review',1)", ("$p", p), ("$r", root));
}
Sql("UPDATE resource_meta SET active=0 WHERE path=$p OR path=$q", ("$p", salt), ("$q", kaguya));
// A split child with its own manual link must never replace its historical bound parent.
var split = Path.Combine(kaguya, "Other"); store.UpsertLocalFile(new LocalFile { Path = split, Name = "Other", RootPath = root, Kind = "folder" });
store.SetFileLink(split, 1234567, 1, "manual", "auto");
Sql("INSERT INTO resource_meta(path,root,category,active) VALUES($p,$r,'0角色/Kaguya',1)", ("$p", split), ("$r", root));
var manualBefore = Scalar("SELECT group_concat(path||':'||item_id,'|') FROM (SELECT path,item_id FROM local_files WHERE matched_by='manual' ORDER BY path)");
library.Repair(root); await Wait();
Check("wrong review category deactivated and children restored", Files().Any(x => x.Path == salt) && Files().All(x => x.Path != actors && x.Path != clothes));
Check("all manual links preserved including hidden split", Scalar("SELECT group_concat(path||':'||item_id,'|') FROM (SELECT path,item_id FROM local_files WHERE matched_by='manual' ORDER BY path)") == manualBefore);
Check("historical bound parent reused and nested split hidden", Files().Count(x => x.Path.StartsWith(kaguya, StringComparison.OrdinalIgnoreCase)) == 1 && library.Get(kaguya)!.ItemId == 1234567 && library.Get(kaguya)!.Skipped);
Check("review candidates preserved on tombstone", library.Get(actors)!.Candidates.Count == 1);
library.ConfigureBoundary(mixed, "asset"); library.Repair(root); await Wait();
Check("explicit asset combines category and hides children", Files().Count(x => x.Path.StartsWith(mixed, StringComparison.OrdinalIgnoreCase)) == 1 && Files().Any(x => x.Path == mixed));
library = new ContainerLibrary(db, store, new Catalog(booth, store, data), booth);
Check("explicit boundary persists new instance", J(library.GetBoundaries()).GetProperty("boundaries").EnumerateArray().Any(x => x.GetProperty("path").GetString() == mixed && x.GetProperty("kind").GetString() == "asset"));
library.ConfigureBoundary(mixed, "category"); library.Repair(root); await Wait();
Check("explicit category restores children", Files().Count(x => x.Path.StartsWith(mixed, StringComparison.OrdinalIgnoreCase)) == 2);
library.ConfigureBoundary(mixed, "auto");
Check("auto removes override", J(library.GetBoundaries()).GetProperty("boundaries").GetArrayLength() == 0);
Check("bounded structure available", J(library.Structure(root)).GetProperty("folders").GetArrayLength() <= 2000);
Check("single canonical root", J(library.ListRoots()).GetProperty("roots").GetArrayLength() == 1);
library.ConfigureBoundary(root, "asset"); library.Repair(root); await Wait();
Check("repair respects explicit root asset", Files().Count == 1 && Files()[0].Path == root);
Check("offline only", booth.RequestCount == 0);
Console.WriteLine("BOUNDARY CHECKS PASSED " + work);
sealed class NoNetwork : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new Exception("Unexpected network request"); }
