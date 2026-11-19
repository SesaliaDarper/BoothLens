using System.Text;
using System.Text.RegularExpressions;

namespace BoothLens.Core.Util;

/// <summary>把 BOOTH 日文标题变成一个 Windows + Unity 都安全的目录名（不做翻译、不改写语义）。</summary>
public static class SafeName
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly Regex SpaceRx = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 规则：非法字符换全角（保留可读性而不是删掉）→ 去控制字符与非 BMP → 折叠空白
    /// → 去尾部点和空格 → 保留字加前缀 → 截断。
    /// </summary>
    public static string Clean(string? title, int maxLen = 48)
    {
        if (string.IsNullOrWhiteSpace(title)) return "untitled";

        var s = title.Normalize(NormalizationForm.FormC);

        s = s.Replace('/', '／').Replace('\\', '＼').Replace(':', '：')
             .Replace('*', '＊').Replace('?', '？').Replace('"', '＂')
             .Replace('<', '＜').Replace('>', '＞').Replace('|', '｜');

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsControl(c)) continue;
            if (char.IsSurrogate(c)) continue;   // emoji 等非 BMP：Unity 的 .meta 和部分解压工具会翻车
            sb.Append(c);
        }
        s = sb.ToString();

        s = SpaceRx.Replace(s, " ").Trim();
        s = s.TrimEnd('.', ' ');
        if (s.Length == 0) return "untitled";

        if (Reserved.Contains(s)) s = "_" + s;

        if (s.Length > maxLen)
        {
            s = s.Substring(0, maxLen).TrimEnd('.', ' ');
            if (s.Length == 0) s = "untitled";
        }
        return s;
    }

    /// <summary>目录名约定：{itemId}_{安全标题}。id 在前，同店铺/同系列会按 id 聚在一起。</summary>
    public static string FolderName(long itemId, string? title) => $"{itemId}_{Clean(title)}";
}
