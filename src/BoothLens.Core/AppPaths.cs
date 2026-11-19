namespace BoothLens.Core;

public sealed class AppPaths
{
    public string Root { get; }

    public string DbPath => Path.Combine(Root, "db.sqlite");
    public string ImageDir => Path.Combine(Root, "images");
    public string StagingDir => Path.Combine(Root, "staging");
    public string LogDir => Path.Combine(Root, "logs");

    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ImageDir);
        Directory.CreateDirectory(StagingDir);
        Directory.CreateDirectory(LogDir);
    }

    public static AppPaths CreateDefault(string? explicitRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return new AppPaths(explicitRoot);

        var env = Environment.GetEnvironmentVariable("BOOTHLENS_DATA");
        if (!string.IsNullOrWhiteSpace(env)) return new AppPaths(env);

        return new AppPaths(Path.Combine(AppContext.BaseDirectory, "data"));
    }
}
