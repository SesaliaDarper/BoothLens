// Install into Assets/Editor/BoothRelay.cs. Unity 2022.3 or later.
// Jobs live outside Assets: Library/BoothLens/{queue,running,done}.
#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BoothLens.Editor
{
    [InitializeOnLoad]
    public static class BoothRelay
    {
        [Serializable] private sealed class Job
        {
            public long jobId;
            public string mode;
            public string source;
            public string targetFolder;
        }
        [Serializable] private sealed class Receipt
        {
            public long jobId;
            public string state;
            public string message;
        }
        [Serializable] private sealed class Heartbeat
        {
            public string id;
            public string projectPath;
            public int processId;
            public long processStartTicks;
            public string updatedUtc;
            public bool isActive;
            public bool busy;
            public string bridgeVersion;
            public long activeJobId;
        }
        private static readonly int ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
        private static readonly long ProcessStartTicks = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        private static readonly string EditorId = ProcessId + "-" + ProcessStartTicks;
        private static readonly string Registry = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoothLens", "Editors");
        private static readonly string HeartbeatPath = Path.Combine(Registry, EditorId + ".json");
        private const string SessionKey = "BoothLens.ActiveJob";
        private static readonly string Project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static readonly string Root = Path.Combine(Project, "Library", "BoothLens");
        private static Job active;
        private static double nextCheck;

        static BoothRelay()
        {
            try
            {
                var json = SessionState.GetString(SessionKey, "");
                if (json.Length > 0) active = JsonUtility.FromJson<Job>(json);
            }
            catch (Exception ex) { Debug.LogWarning("BoothLens restore: " + ex.Message); }
            AssetDatabase.importPackageCompleted += Completed;
            AssetDatabase.importPackageCancelled += Cancelled;
            AssetDatabase.importPackageFailed += Failed;
            EditorApplication.update += Tick;
            EditorApplication.quitting += RemoveHeartbeat;
            PublishHeartbeat();
        }

        private static void PublishHeartbeat()
        {
            try
            {
                Directory.CreateDirectory(Registry);
                CheckNoLinks(Registry);
                var status = new Heartbeat { id = EditorId, projectPath = Project, processId = ProcessId,
                    processStartTicks = ProcessStartTicks, updatedUtc = DateTime.UtcNow.ToString("O"),
                    isActive = UnityEditorInternal.InternalEditorUtility.isApplicationActive,
                    bridgeVersion = "0.2.0", activeJobId = active != null ? active.jobId : 0,
                    busy = active != null || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode };
                var temp = HeartbeatPath + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(status));
                if (File.Exists(HeartbeatPath)) File.Replace(temp, HeartbeatPath, null);
                else File.Move(temp, HeartbeatPath);
            }
            catch (Exception ex) { Debug.LogWarning("BoothLens heartbeat: " + ex.Message); }
        }
        private static void RemoveHeartbeat()
        {
            try { if (File.Exists(HeartbeatPath)) File.Delete(HeartbeatPath); }
            catch (Exception ex) { Debug.LogWarning("BoothLens heartbeat cleanup: " + ex.Message); }
        }

        private static bool warnedPending;
        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextCheck) return;
            nextCheck = EditorApplication.timeSinceStartup + 2;
            PublishHeartbeat();
            if (active != null || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var queue = Path.Combine(Root, "queue");
            if (!Directory.Exists(queue)) return;
            string runningFile = null;
            try
            {
                CheckNoLinks(queue);
                // A stale running job needs explicit resolution; never silently repeat a package import.
                var running = Path.Combine(Root, "running");
                if (Directory.Exists(running) && Directory.EnumerateFiles(running, "*.json").Any())
                {
                    if (!warnedPending) { Debug.LogWarning("BoothLens: 有未完成的导入任务阻塞队列。请回到 BoothLens 导入窗口查看并清理旧会话队列，或使用 Tools/BoothLens/Resolve pending import。"); warnedPending = true; }
                    return;
                }
                warnedPending = false;
                var file = Directory.EnumerateFiles(queue, "*.json").OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
                if (file == null) return;
                CheckNoLinks(file);
                if (new FileInfo(file).Length > 65536) throw new IOException("Job JSON is too large.");
                var job = JsonUtility.FromJson<Job>(File.ReadAllText(file));
                if (job == null || job.jobId <= 0 || Path.GetFileNameWithoutExtension(file) != job.jobId.ToString())
                    throw new IOException("Invalid BoothLens job id or filename.");
                Directory.CreateDirectory(running);
                CheckNoLinks(running);
                runningFile = Path.Combine(running, Path.GetFileName(file));
                File.Move(file, runningFile);
                active = job;
                SessionState.SetString(SessionKey, JsonUtility.ToJson(job));
                if (job.mode == "refresh")
                {
                    var relative = (job.targetFolder ?? "").Replace('\\', '/');
                    var target = Path.GetFullPath(Path.Combine(Project, relative));
                    if (!relative.StartsWith("Assets/", StringComparison.Ordinal) || !Under(Application.dataPath, target))
                        throw new IOException("Refresh target is outside Assets.");
                    CheckNoLinks(target);
                    AssetDatabase.Refresh();
                    var asset = AssetDatabase.LoadMainAssetAtPath(relative);
                    if (asset != null) EditorGUIUtility.PingObject(asset);
                    Finish("done", "Unity 已刷新资源：" + relative);
                }
                else if (job.mode == "unitypackage")
                {
                    if (string.IsNullOrWhiteSpace(job.source) || !Path.IsPathRooted(job.source) ||
                        !job.source.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase) || !File.Exists(job.source))
                        throw new IOException("Unity package source is missing or invalid.");
                    CheckNoLinks(job.source);
                    // Interactive dialog retains the original package paths and GUIDs.
                    Debug.Log("BoothLens: 正在打开 Unity 原生包导入窗口，请切换到 Unity 确认 Import。任务 #" + job.jobId);
                    AssetDatabase.ImportPackage(job.source, true);
                }
                else throw new IOException("Unsupported BoothLens job mode.");
            }
            catch (Exception ex)
            {
                Debug.LogError("BoothLens: " + ex.Message);
                if (active != null) Finish("failed", ex.Message);
                else if (runningFile != null) Debug.LogError("Resolve pending job from Tools/BoothLens.");
            }
        }

        private static void Completed(string packageName)
        {
            if (MatchesPackage(packageName)) Finish("done", "Unity 包导入完成：" + packageName);
        }
        private static void Cancelled(string packageName)
        {
            if (MatchesPackage(packageName)) Finish("failed", "用户取消了 Unity 包导入。");
        }
        private static void Failed(string packageName, string error)
        {
            if (MatchesPackage(packageName)) Finish("failed", error);
        }
        private static bool MatchesPackage(string packageName)
        {
            return active != null && active.mode == "unitypackage" &&
                string.Equals(PackageNames.Normalize(active.source), PackageNames.Normalize(packageName), StringComparison.OrdinalIgnoreCase);
        }
        private static void Finish(string state, string message)
        {
            if (active == null) return;
            try
            {
                var done = Path.Combine(Root, "done");
                Directory.CreateDirectory(done);
                CheckNoLinks(done);
                var output = Path.Combine(done, active.jobId + ".json");
                var temp = output + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(new Receipt { jobId = active.jobId, state = state, message = message }));
                if (File.Exists(output)) File.Delete(output);
                File.Move(temp, output);
                var running = Path.Combine(Root, "running", active.jobId + ".json");
                if (File.Exists(running)) File.Delete(running);
                active = null;
                SessionState.EraseString(SessionKey);
            }
            catch (Exception ex) { Debug.LogError("BoothLens receipt failed: " + ex.Message); }
        }

        [MenuItem("Tools/BoothLens/Resolve pending import")]
        private static void Resolve()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (!EditorUtility.DisplayDialog("BoothLens", "仅在原导入已经停止、没有 Import Unity Package 窗口时使用。取消当前记录及全部等待任务，避免连续弹出重复导入；不会删除项目资源。之后回到 BoothLens 重新提交一次。", "确认已停止并清理", "返回")) return;
            var saved = active;
            try
            {
                var queue = Path.Combine(Root, "queue");
                var files = Directory.Exists(queue) ? Directory.EnumerateFiles(queue, "*.json").ToArray() : new string[0];
                // Cancel queued duplicates BEFORE releasing the running barrier.
                foreach (var file in files)
                {
                    CheckNoLinks(file);
                    if (new FileInfo(file).Length > 65536) throw new IOException("Pending JSON is too large.");
                    var job = JsonUtility.FromJson<Job>(File.ReadAllText(file));
                    if (job == null || job.jobId <= 0 || Path.GetFileNameWithoutExtension(file) != job.jobId.ToString()) throw new IOException("Invalid pending job.");
                    active = job;
                    Finish("failed", "用户清理了等待队列，避免重复导入；可按需重新提交。");
                    if (active != null) throw new IOException("Failed to save cancellation receipt.");
                    File.Delete(file);
                }
                active = saved;
                var running = Path.Combine(Root, "running");
                if (active == null && Directory.Exists(running))
                {
                    var file = Directory.EnumerateFiles(running, "*.json").FirstOrDefault();
                    if (file != null) { CheckNoLinks(file); active = JsonUtility.FromJson<Job>(File.ReadAllText(file)); }
                }
                Finish("failed", "用户手动结束了待处理的导入记录。");
            }
            catch (Exception ex)
            {
                active = saved;
                if (saved != null) SessionState.SetString(SessionKey, JsonUtility.ToJson(saved));
                Debug.LogError("BoothLens queue recovery: " + ex.Message);
            }
        }
        private static bool Under(string root, string path)
        {
            return path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                Application.platform == RuntimePlatform.WindowsEditor ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        private static void CheckNoLinks(string path)
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked paths are not supported: " + current);
                current = Path.GetDirectoryName(current);
            }
        }
    }
}
#endif

// Pure helper shared with regression tests; only the actual package suffix is removed.
namespace BoothLens.Editor
{
    internal static class PackageNames
    {
        internal static string Normalize(string value)
        {
            var name = System.IO.Path.GetFileName((value ?? "").Replace('\\', '/'));
            const string extension = ".unitypackage";
            return name.EndsWith(extension, System.StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - extension.Length) : name;
        }
    }
}
