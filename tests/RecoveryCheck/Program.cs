using System.Text.Json;
using BoothLens.Core;
using BoothLens.Core.Data;
using BoothLens.Core.Library;

var root = Path.Combine(Path.GetTempPath(), "BoothLensRecovery-" + Guid.NewGuid().ToString("N"));
var registry = Path.Combine(root, "registry"); Directory.CreateDirectory(registry);
var project = Path.Combine(root, "project");
Directory.CreateDirectory(Path.Combine(project, "Assets"));
Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "2022.3");
var assets = Path.Combine(project, "Assets", "keep.txt"); File.WriteAllText(assets, "untouched asset");
var source = Path.Combine(root, "source.unitypackage"); File.WriteAllText(source, "untouched source");
var paths = new AppPaths(Path.Combine(root, "data"));
var db = new Db(paths.DbPath); db.EnsureSchema(); var store = new ItemStore(db);
var importer = new Importer(paths, store);
var start = DateTime.UtcNow.AddMinutes(-5).Ticks;
var live = true;
var editors = new UnityEditors(registry, (pid, ticks) => live && pid == 101 && ticks == start);
var editorId = "101-" + start;
void Heartbeat(bool busy = false, int age = 0) => File.WriteAllText(Path.Combine(registry, editorId + ".json"),
    JsonSerializer.Serialize(new { id = editorId, projectPath = project, processId = 101, processStartTicks = start,
        updatedUtc = DateTimeOffset.UtcNow.AddSeconds(-age).ToString("O"), isActive = true, busy }));
string Record(string folder, long id) => Path.Combine(project, "Library", "BoothLens", folder, id + ".json");
long Job(string folder, bool old = true, string? target = null)
{
    var id = store.CreateJob(null, source, "unitypackage");
    store.UpdateJob(id, "queued", "fixture", target ?? project);
    var file = Record(folder, id); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.WriteAllText(file, JsonSerializer.Serialize(new { jobId = id, mode = "unitypackage", source, targetFolder = "" }));
    File.SetLastWriteTimeUtc(file, new DateTime(old ? start - TimeSpan.TicksPerDay : start, DateTimeKind.Utc));
    return id;
}
void Check(string name, bool value) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
void Reject(string name, Action action)
{
    try { action(); } catch (Exception ex) when (ex is ArgumentException or IOException or JsonException) { Console.WriteLine("PASS " + name); return; }
    throw new Exception("Expected rejection: " + name);
}
UnityEditors.OpenProject Recover() => editors.ResolveForRecovery(editorId);
Importer.ResetPendingResult Reset() => importer.ResetPending(Recover());
void Remove(string folder, long id) => File.Delete(Record(folder, id));

Heartbeat();
Check("clean live project resolves", editors.Resolve(editorId).Busy == false);
var oldId = Job("running"); var queued1 = Job("queue"); var queued2 = Job("queue");
var projectInfo = editors.List().Single();
Check("raw idle heartbeat plus stale running is busy and recoverable", projectInfo.Busy && projectInfo.CanResetPending && projectInfo.PendingJobId == oldId && projectInfo.BlockReason != null);
Reject("ordinary import resolve blocked by stale records", () => editors.Resolve(editorId));
importer.Poll(project);
Check("listing and polling never replay or cancel", File.Exists(Record("running", oldId)) && File.Exists(Record("queue", queued1)) && store.GetJob(oldId)!.State == "queued");
var result = Reset();
Check("reset cancels old running plus every queued job", result.CancelledJobs.SequenceEqual(new[] { oldId, queued1, queued2 }));
Check("failed receipts applied through Poll", new[] { oldId, queued1, queued2 }.All(id => store.GetJob(id)!.State == "failed"));
Check("records archived, queue and running empty", File.Exists(Record("cancelled/running", oldId)) && File.Exists(Record("cancelled/queue", queued1)) && !Directory.EnumerateFiles(Path.GetDirectoryName(Record("queue", queued1))!, "*.json").Any() && !Directory.EnumerateFiles(Path.GetDirectoryName(Record("running", oldId))!, "*.json").Any());
Check("repeat recovery is idempotent", Reset().CancelledJobs.Count == 0);
Check("source and Assets preserved, no replay into Assets", File.ReadAllText(source) == "untouched source" && File.ReadAllText(assets) == "untouched asset" && Directory.EnumerateFileSystemEntries(Path.Combine(project, "Assets")).Count() == 1);
Check("project available after reset", !editors.Resolve(editorId).Busy);

var current = Job("running", old: false); var waiting = Job("queue");
Check("mtime equal to start is current-session and hidden reset", !editors.List().Single().CanResetPending);
Reject("same-session running blocks whole reset", () => Reset());
Check("same-session rejection leaves queue and job unchanged", File.Exists(Record("queue", waiting)) && store.GetJob(waiting)!.State == "queued");
Remove("running", current); Remove("queue", waiting);
var stale = Job("running"); var queue = Job("queue"); Heartbeat(busy: true);
Check("raw busy heartbeat hides reset", !editors.List().Single().CanResetPending);
Reject("raw busy heartbeat blocks recovery", () => Reset());
Heartbeat(); var validated = Recover(); Heartbeat(busy: true);
Reject("fresh validation rechecks raw busy after Resolve", () => importer.ResetPending(validated));
Heartbeat(); File.SetLastWriteTimeUtc(Record("running", stale), new DateTime(start, DateTimeKind.Utc));
Reject("recovery rechecks running time", () => importer.ResetPending(validated));
File.SetLastWriteTimeUtc(Record("running", stale), new DateTime(start - TimeSpan.TicksPerDay, DateTimeKind.Utc));
Heartbeat(age: 60); Reject("expired heartbeat rejected", () => importer.ResetPending(validated));
Heartbeat(); live = false; Reject("exited process rejected", () => importer.ResetPending(validated)); live = true;
Reject("caller constructed project has no recovery authority", () => importer.ResetPending(new(editorId, project, "project", 101, true, true, DateTimeOffset.UtcNow.ToString("O"), false)));
Reject("record copy cannot change validated project scope", () => importer.ResetPending(validated with { ProjectPath = root }));

var invalid = Path.Combine(project, "Library", "BoothLens", "queue", "999999.json");
File.WriteAllText(invalid, "{\"jobId\":1,\"mode\":\"unitypackage\"}");
Reject("filename and payload job ID must match", () => Reset());
Check("batch preflight prevents partial cancellation", File.Exists(Record("queue", queue)) && File.Exists(Record("running", stale)));
File.WriteAllText(invalid, "{\"jobId\":999999,\"mode\":\"unitypackage\"}"); Reject("unknown job ID rejected", () => Reset());
File.WriteAllText(invalid, new string('x', 65537)); Reject("oversized JSON rejected before parse", () => Reset()); File.Delete(invalid);
var foreign = Job("queue", target: Path.Combine(root, "other-project"));
Reject("job target must belong to selected project", () => Reset()); Remove("queue", foreign);
Reset();
var onlyQueue = Job("queue");
Check("queue only is busy but not recoverable", editors.List().Single().Busy && !editors.List().Single().CanResetPending);
Reject("queue-only reset blocked to avoid bridge race", () => Reset()); Remove("queue", onlyQueue);

// Simulate interruption after archival but before receipt publication.
var interrupted = Job("running"); Directory.CreateDirectory(Path.GetDirectoryName(Record("cancelled/running", interrupted))!);
File.Move(Record("running", interrupted), Record("cancelled/running", interrupted));
Check("archived interrupted reset resumes receipt and Poll", Reset().CancelledJobs.Contains(interrupted) && store.GetJob(interrupted)!.State == "failed");
var malformed = Job("running");
File.WriteAllText(Record("running", malformed), "not json");
Check("any running JSON blocks even malformed content", editors.List().Single().Busy);
Reject("malformed JSON never cleared", () => Reset()); Remove("running", malformed);

// NoLinks applies to the record itself and every parent component.
var linkedJob = Job("running"); var link = Path.Combine(project, "Library", "BoothLens", "queue", "999998.json");
try
{
    File.CreateSymbolicLink(link, source);
    Reject("linked queue record rejected", () => Reset());
    Check("linked rejection preserves source", File.ReadAllText(source) == "untouched source");
    File.Delete(link);
}
catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException || ex is IOException io && (io.HResult & 0xffff) == 1314) { Console.WriteLine("SKIP symlink creation privilege unavailable"); }
Remove("running", linkedJob);
Console.WriteLine("RECOVERY CHECKS PASSED; temp fixture only, no real Unity project touched");
