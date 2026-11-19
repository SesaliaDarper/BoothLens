using System.Text.Json;

namespace BoothLens.Core;

public sealed class AppSettings
{
    /// <summary>Unity 工程根目录；留空则只做“解压到 staging”，由用户手动拖。</summary>
    public string? UnityProjectPath { get; set; }

    /// <summary>导入到 Assets 下的子目录名。</summary>
    public string AssetsSubfolder { get; set; } = "_BoothItems";

    /// <summary>本地资源扫描根目录。</summary>
    public List<string> ScanRoots { get; set; } = new();

    /// <summary>两次 BOOTH 请求之间的最小间隔（毫秒）。</summary>
    public int ThrottleMs { get; set; } = 1200;

    /// <summary>BOOTH 登录 cookie（Cookie 请求头原文），拉已购列表和 R-18 需要。</summary>
    public string? CookieHeader { get; set; }

    /// <summary>默认导入模式：copy | unitypackage | uri</summary>
    public string ImportMode { get; set; } = "copy";

    public double AutoThreshold { get; set; } = 0.72;
    public double ReviewThreshold { get; set; } = 0.45;

    /// <summary>拿不准时用它给“未识别”文件做一条兜底推断的上限条数。</summary>
    public int MatchCandidateLimit { get; set; } = 4000;

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    /// <summary>对外输出时遮掉 cookie。</summary>
    public object ToSafeView() => new
    {
        unityProjectPath = UnityProjectPath,
        assetsSubfolder = AssetsSubfolder,
        scanRoots = ScanRoots,
        throttleMs = ThrottleMs,
        importMode = ImportMode,
        autoThreshold = AutoThreshold,
        reviewThreshold = ReviewThreshold,
        hasCookie = !string.IsNullOrWhiteSpace(CookieHeader)
    };
}
