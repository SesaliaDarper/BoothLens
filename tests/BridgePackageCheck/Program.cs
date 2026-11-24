using BoothLens.Core.Library;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "BoothLensBridgePackages-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
var legacy = Bytes("namespace BoothLens.Editor { public static class BoothRelay {} } // known v1");
var legacyHash = Convert.ToHexString(SHA256.HashData(legacy));
Dictionary<string, byte[]> Package(string version) => new()
{
    ["package.json"] = JsonSerializer.SerializeToUtf8Bytes(new { name = UnityBridgePackages.PackageName, version }),
    ["Editor/BoothRelay.cs"] = Bytes("namespace BoothLens.Editor { public class BoothRelay {} } // " + version),
    ["Editor/BoothLens.Editor.asmdef"] = Bytes("{\"name\":\"BoothLens.Editor\",\"includePlatforms\":[\"Editor\"]}")
};
UnityBridgePackages Manager(string version = "1.0.0", Action<string>? fault = null) => new(Package(version), new[] { legacyHash }, checkpoint: fault);
string Project(string name)
{
    var project = Path.Combine(root, name);
    foreach (var folder in new[] { "Assets", "Packages", "ProjectSettings" }) Directory.CreateDirectory(Path.Combine(project, folder));
    File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.22f1");
    File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), "{\n  \"dependencies\": {\"com.unity.test\":\"1.0\"}\n}\n");
    return project;
}
string Pkg(string project) => Path.Combine(project, "Packages", UnityBridgePackages.PackageName);
void Check(string name, bool ok) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
string AddLegacy(string project)
{
    var file = Path.Combine(project, "Assets", "Editor", "BoothRelay.cs"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.WriteAllBytes(file, legacy); File.WriteAllText(file + ".meta", "guid: legacy-guid"); return file;
}
try
{
    var manager = Manager(); var project = Project("normal");
    var manifest = File.ReadAllBytes(Path.Combine(project, "Packages", "manifest.json"));
    var source = Path.Combine(project, "Assets", "User.cs"); File.WriteAllText(source, "// user script");
    File.WriteAllBytes(Path.Combine(project, "Assets", "large.bin"), new byte[256 * 1024]);
    var old = AddLegacy(project);
    Check("status is read-only and recognizes exact legacy bytes", manager.GetStatus(project) is { Installed: false, State: "not-installed", LegacyScripts.Count: 1 } && !Directory.Exists(Path.Combine(project, "Library")));
    var installed = manager.Install(project);
    Check("offline embedded install succeeds", installed is { State: "installed", Installed: true, Managed: true, Version: "1.0.0" } && File.Exists(Path.Combine(Pkg(project), "package.json")));
    Check("legacy and meta archived with relative paths", !File.Exists(old) && File.ReadAllBytes(Path.Combine(installed.BackupPath!, "legacy", "Assets", "Editor", "BoothRelay.cs")).SequenceEqual(legacy) && File.Exists(Path.Combine(installed.BackupPath!, "legacy", "Assets", "Editor", "BoothRelay.cs.meta")));
    var firstBackups = Directory.GetDirectories(Path.Combine(project, "Library", "BoothLens", "bridge-backups")).Length;
    Check("repeat install is idempotent", manager.Install(project).State == "installed" && firstBackups == Directory.GetDirectories(Path.Combine(project, "Library", "BoothLens", "bridge-backups")).Length);
    var manager2 = Manager("2.0.0");
    Check("new embedded version reports update", manager2.GetStatus(project).State == "update-available");
    File.WriteAllText(Path.Combine(Pkg(project), "custom.user"), "preserve extra");
    var update = manager2.Install(project);
    Check("update archives whole old package with user extras", update.Version == "2.0.0" && File.ReadAllText(Path.Combine(update.BackupPath!, "package", "custom.user")) == "preserve extra");
    File.WriteAllText(Path.Combine(Pkg(project), "another.user"), "uninstall extra");
    var uninstall = manager2.Uninstall(project);
    Check("uninstall archives instead of deleting", uninstall is { Installed: false, State: "not-installed" } && !Directory.Exists(Pkg(project)) && File.ReadAllText(Path.Combine(uninstall.BackupPath!, "package", "another.user")) == "uninstall extra");
    Check("repeat uninstall idempotent", manager2.Uninstall(project).State == "not-installed");
    Check("manifest and unrelated Assets untouched", File.ReadAllBytes(Path.Combine(project, "Packages", "manifest.json")).SequenceEqual(manifest) && File.ReadAllText(source) == "// user script" && File.Exists(Path.Combine(project, "Assets", "large.bin")));

    var unknown = Project("unknown"); var unknownScript = AddLegacy(unknown); File.AppendAllText(unknownScript, " // changed");
    Check("unknown same named script blocks without modifications", manager.Install(unknown).State == "conflict" && File.Exists(unknownScript) && !Directory.Exists(Path.Combine(unknown, "Library")));
    File.Move(unknownScript, Path.Combine(Path.GetDirectoryName(unknownScript)!, "Renamed.cs"));
    Check("renamed duplicate namespace type blocks", manager.Install(unknown).State == "conflict");
    var manual = Project("manual"); Directory.CreateDirectory(Pkg(manual)); File.WriteAllText(Path.Combine(Pkg(manual), "package.json"), "{}");
    Check("manual same-name UPM package is preserved", manager.Install(manual).State == "conflict" && File.ReadAllText(Path.Combine(Pkg(manual), "package.json")) == "{}");
    var dependency = Project("dependency"); File.WriteAllText(Path.Combine(dependency, "Packages", "manifest.json"), "{\"dependencies\":{\"com.boothlens.bridge\":\"file:../external\"}}");
    Check("external manifest dependency blocks", manager.Install(dependency).State == "conflict" && !Directory.Exists(Pkg(dependency)));
    var changed = Project("modified"); manager.Install(changed); File.AppendAllText(Path.Combine(Pkg(changed), "Editor", "BoothRelay.cs"), "// user change");
    Check("modified tracked package blocks update and uninstall", manager2.Install(changed).State == "conflict" && manager.Uninstall(changed).State == "conflict" && Directory.Exists(Pkg(changed)));
    Check("missing project blocked", manager.Install(Path.Combine(root, "missing")).State == "invalid-project");
    Check("filesystem root blocked", !manager.Install(Path.GetPathRoot(root)!).Installed);
    var noVersion = Project("no-version"); File.Delete(Path.Combine(noVersion, "ProjectSettings", "ProjectVersion.txt"));
    Check("missing ProjectVersion blocked", !manager.Install(noVersion).Installed && !Directory.Exists(Path.Combine(noVersion, "Library")));
    var noAssets = Project("no-assets"); Directory.Delete(Path.Combine(noAssets, "Assets"));
    Check("missing Assets blocked", !manager.Install(noAssets).Installed && !Directory.Exists(Path.Combine(noAssets, "Library")));
    var incomplete = Project("incomplete"); Directory.Delete(Path.Combine(incomplete, "Packages"), true);
    Check("missing required Packages blocked", !manager.Install(incomplete).Installed && !Directory.Exists(Path.Combine(incomplete, "Library")));
    var oldUnity = Project("old-unity"); File.WriteAllText(Path.Combine(oldUnity, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2021.3.1f1");
    Check("old Unity version gets actionable block", manager.Install(oldUnity).Message.Contains("2022.3") && !Directory.Exists(Path.Combine(oldUnity, "Library")));
    var generated = Project("generated"); File.WriteAllText(Path.Combine(generated, "Assets", "Generated.cs"), "// " + new string('x', 200 * 1024));
    Check("200KiB generated C# source permits normal install", manager.Install(generated).Installed);
    var oversized = Project("oversized"); File.WriteAllText(Path.Combine(oversized, "Assets", "Big.cs"), new string('x', 8 * 1024 * 1024 + 1));
    Check("over-8MiB C# file blocks with concrete path", manager.Install(oversized) is { State: "conflict" } bigStatus && bigStatus.Message.Contains("Big.cs"));
    var overBudget = Project("over-budget");
    for (var n = 0; n < 9; n++) File.WriteAllText(Path.Combine(overBudget, "Assets", $"Generated{n}.cs"), new string('x', 8 * 1024 * 1024));
    Check("total C# read budget bounded at 64MiB", manager.Install(overBudget) is { State: "conflict" } budgetStatus && budgetStatus.Message.Contains("64MiB"));
    var malformed = Project("malformed"); File.WriteAllText(Path.Combine(malformed, "Packages", "manifest.json"), "{");
    Check("malformed manifest blocks before mutation", manager.Install(malformed).State == "conflict" && !Directory.Exists(Pkg(malformed)));

    foreach (var stage in new[] { "staged", "old-package-archived", "legacy-archived", "package-installed" })
    {
        var rollback = Project("rollback-" + stage); Manager().Install(rollback); var script = AddLegacy(rollback);
        var before = File.ReadAllBytes(Path.Combine(Pkg(rollback), "Editor", "BoothRelay.cs"));
        var failed = Manager("2.0.0", point => { if (point == stage) throw new IOException("injected fault at " + stage); }).Install(rollback);
        Check("rollback restores old package + legacy + meta at " + stage, failed.State == "conflict" && File.ReadAllBytes(Path.Combine(Pkg(rollback), "Editor", "BoothRelay.cs")).SequenceEqual(before) && File.ReadAllBytes(script).SequenceEqual(legacy) && File.Exists(script + ".meta"));
    }
    var rollbackInstall = Project("rollback-first-install"); var firstScript = AddLegacy(rollbackInstall);
    var rollbackStatus = Manager(fault: point => { if (point == "package-installed") throw new IOException("fault"); }).Install(rollbackInstall);
    Check("first install rollback restores legacy without publishing package", rollbackStatus.State == "conflict" && !Directory.Exists(Pkg(rollbackInstall)) && File.Exists(firstScript));
    var busyProject = Project("busy"); var busy = new UnityBridgePackages(Package("1.0.0"), new[] { legacyHash }, () => new[] { new UnityEditors.OpenProject("test", busyProject, "busy", 1, true, true, "now", true, "pending stale task") });
    Check("matching busy editor blocks with recovery guidance", busy.Install(busyProject) is { State: "busy" } result && result.Message.Contains("恢复") && !Directory.Exists(Path.Combine(busyProject, "Library")));
    var unrelated = Project("not-busy"); Check("unrelated busy editor does not block selected project", busy.Install(unrelated).Installed);
    var becameBusy = Project("became-busy"); var shouldBeBusy = false;
    var raceBusy = new UnityBridgePackages(Package("1.0.0"), new[] { legacyHash }, () => new[] { new UnityEditors.OpenProject("test", becameBusy, "busy", 1, true, true, "now", shouldBeBusy) }, point => { if (point == "staged") shouldBeBusy = true; });
    Check("busy revalidated after staging before moving source", raceBusy.Install(becameBusy).State == "conflict" && !Directory.Exists(Pkg(becameBusy)));

    // Junctions work without Developer Mode on Windows; Unix uses a directory symlink.
    var linked = Project("linked"); var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
    var link = Path.Combine(linked, "Assets", "Linked");
    if (OperatingSystem.IsWindows())
    {
        using var cmd = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, outside }, UseShellExecute = false, CreateNoWindow = true });
        cmd!.WaitForExit(); if (cmd.ExitCode != 0) throw new Exception("junction creation failed");
    }
    else Directory.CreateSymbolicLink(link, outside);
    Check("Assets reparse point blocked without following target", manager.Install(linked).State == "conflict" && !Directory.Exists(Path.Combine(linked, "Library")));
    Directory.Delete(link);
    var redirectedLibrary = Project("linked-library"); var libLink = Path.Combine(redirectedLibrary, "Library");
    if (OperatingSystem.IsWindows())
    {
        using var cmd = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", libLink, outside }, UseShellExecute = false, CreateNoWindow = true }); cmd!.WaitForExit(); if (cmd.ExitCode != 0) throw new Exception("junction creation failed");
    }
    else Directory.CreateSymbolicLink(libLink, outside);
    Check("future staging ancestor reparse blocked", manager.Install(redirectedLibrary).State == "conflict" && !Directory.Exists(Path.Combine(outside, "BoothLens")));
    Directory.Delete(libLink);
    if (args.Length == 2 && args[0] == "--api")
    {
        using var http = new HttpClient { BaseAddress = new Uri(args[1]) };
        var apiProject = Project("api-smoke"); var request = JsonSerializer.Serialize(new { projectPath = apiProject });
        using var csrf = await http.PostAsync("/api/unity/bridge/install", new StringContent(request, Encoding.UTF8, "application/json"));
        Check("API POST requires existing CSRF token", (int)csrf.StatusCode == 403 && !Directory.Exists(Pkg(apiProject)));
        using var session = JsonDocument.Parse(await http.GetStringAsync("/api/session"));
        http.DefaultRequestHeaders.Add("X-BoothLens-Token", session.RootElement.GetProperty("token").GetString());
        using var response = await http.PostAsync("/api/unity/bridge/install", new StringContent(request, Encoding.UTF8, "application/json"));
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check("API embedded resource install returns expected contract", response.IsSuccessStatusCode && payload.RootElement.GetProperty("installed").GetBoolean() && payload.RootElement.GetProperty("version").GetString() == "0.3.0" && payload.RootElement.GetProperty("state").GetString() == "installed");
        using var state = JsonDocument.Parse(await http.GetStringAsync("/api/unity/bridge?projectPath=" + Uri.EscapeDataString(apiProject)));
        Check("API GET sees managed installation", state.RootElement.GetProperty("managed").GetBoolean());
        using var removal = await http.PostAsync("/api/unity/bridge/uninstall", new StringContent(request, Encoding.UTF8, "application/json"));
        using var removed = JsonDocument.Parse(await removal.Content.ReadAsStringAsync());
        Check("API uninstall archives embedded package", removal.IsSuccessStatusCode && !removed.RootElement.GetProperty("installed").GetBoolean() && Directory.Exists(removed.RootElement.GetProperty("backupPath").GetString()));
    }
    Console.WriteLine("BRIDGE PACKAGE CHECKS PASSED; only temporary fake Unity projects touched");
}
finally { Directory.Delete(root, true); }
