using System.Diagnostics;
using BoothLens.Core.Data;

namespace BoothLens.Core.Library;
public static class ResourceLocations
{
    public static string Validate(string path, Db db, ItemStore store, ContainerLibrary local)
    {
        path = ResourcePaths.Full(path);
        var registered = store.GetLocalFile(path) != null;
        if (!registered)
        {
            using var c = db.Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT path FROM resource_meta WHERE active=1";
            using var reader = cmd.ExecuteReader();
            while (!registered && reader.Read())
            {
                var r = local.Get(reader.GetString(0));
                registered = r != null && (r.Aliases.Contains(path, ResourcePaths.Comparer) || r.Members.Any(m => ResourcePaths.Comparer.Equals(m.Path, path)));
            }
        }
        if (!registered) throw new ArgumentException("请选择已扫描资源或其包成员所在位置。");
        ResourcePaths.NoLinks(path);
        return path;
    }
    public static void Reveal(string path, Db db, ItemStore store, ContainerLibrary local)
    {
        path = Validate(path, db, store, local);
        if (!OperatingSystem.IsWindows()) throw new ArgumentException("当前打开所在位置功能适用于 Windows。");
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        if (Directory.Exists(path)) start.ArgumentList.Add(path);
        else { start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path); }
        Process.Start(start);
    }
}
