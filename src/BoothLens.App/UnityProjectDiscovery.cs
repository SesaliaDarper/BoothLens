using System.Management;
using System.Text.RegularExpressions;
using BoothLens.Core.Library;

internal static partial class UnityProjectDiscovery
{
    [GeneratedRegex("(?:^|\\s)-projectPath\\s+(?:\"([^\"]+)\"|([^\\s]+))", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectArgument();
    public static object List(UnityEditors editors)
    {
        var entries = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        string? warning = null;
        foreach (var p in editors.List()) entries[p.ProjectPath] = new { projectPath = p.ProjectPath, name = p.Name, processId = p.ProcessId, connected = true, source = "bridge" };
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var search = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='Unity.exe'");
                using var result = search.Get();
                foreach (ManagementObject process in result)
                {
                    using (process)
                    {
                        var match = ProjectArgument().Match(process["CommandLine"]?.ToString() ?? "");
                        if (!match.Success) continue;
                        var raw = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                        try
                        {
                            var path = Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            if (entries.ContainsKey(path) || !Directory.Exists(Path.Combine(path, "Assets")) || !File.Exists(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt"))) continue;
                            entries[path] = new { projectPath = path, name = Path.GetFileName(path), processId = Convert.ToInt32(process["ProcessId"]), connected = false, source = "process" };
                        }
                        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
                    }
                }
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
            { warning = "自动发现暂不可用，请使用“选择工程文件夹”。"; }
        }
        return new { projects = entries.Values.ToArray(), message = warning ?? "列出正在运行的 Unity 工程；未出现时可选择工程文件夹。安装组件不等于已连接，需等待 Unity 编译和心跳。" };
    }
}
