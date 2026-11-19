using System.Text;
using System.Text.RegularExpressions;

namespace BoothLens.Core.Util;

/// <summary>
/// 日文标题没有词边界，所以匹配用字符二元组 Jaccard，而不是分词。
/// </summary>
public static class TextNorm
{
    // v1.2 / 1.0.3 / v2
    private static readonly Regex VersionRx = new(
        @"(?<![0-9])v?\d+(?:\.\d+)+(?:[a-z])?(?![0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 分隔符与括号一律去掉（括号内容保留，因为真实品名常写在括号里）
    private static readonly Regex SepRx = new(
        @"[\s\u3000_\-‐‑–—・.,:;!?/\\|~`'""^&+=*#$%()\[\]{}<>「」『』【】（）〔〕]+",
        RegexOptions.Compiled);

    private static readonly Regex TailRx = new(
        @"(?:コピー|完成版|最新版|修正版|のコピー)$",
        RegexOptions.Compiled);

    public static string Nfkc(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.Normalize(NormalizationForm.FormKC);

    /// <summary>用于搜索与匹配的指纹。</summary>
    public static string Fingerprint(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = Nfkc(s).ToLowerInvariant();
        t = VersionRx.Replace(t, " ");
        t = SepRx.Replace(t, "");
        t = TailRx.Replace(t, "");
        return t.Trim();
    }

    public static HashSet<string> Bigrams(string fingerprint)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (fingerprint.Length == 0) return set;
        if (fingerprint.Length == 1)
        {
            set.Add(fingerprint);
            return set;
        }
        for (var i = 0; i + 1 < fingerprint.Length; i++)
            set.Add(fingerprint.Substring(i, 2));
        return set;
    }

    public static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var (small, big) = a.Count <= b.Count ? (a, b) : (b, a);
        var inter = 0;
        foreach (var x in small)
            if (big.Contains(x)) inter++;
        return (double)inter / (a.Count + b.Count - inter);
    }

    /// <summary>0~1 的相似度。包含关系给一个下限，避免短文件名被稀释。</summary>
    public static double Score(string? a, string? b)
    {
        var fa = Fingerprint(a);
        var fb = Fingerprint(b);
        if (fa.Length == 0 || fb.Length == 0) return 0;
        if (fa == fb) return 1;

        var j = Jaccard(Bigrams(fa), Bigrams(fb));

        if (fa.Length >= 4 && fb.Contains(fa, StringComparison.Ordinal))
            j = Math.Max(j, 0.82);
        else if (fb.Length >= 4 && fa.Contains(fb, StringComparison.Ordinal))
            j = Math.Max(j, 0.82);

        return j;
    }

    public static string? ExtractItemId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"items/(\d{5,9})");
        if (m.Success) return m.Groups[1].Value;
        var m2 = Regex.Match(text.Trim(), @"^(\d{5,9})$");
        return m2.Success ? m2.Groups[1].Value : null;
    }

    /// <summary>从任意文本里抽所有 5~9 位数字，当作候选 itemId。</summary>
    public static IEnumerable<long> ExtractDigitCandidates(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (Match m in Regex.Matches(text, @"(?<!\d)(\d{5,9})(?!\d)"))
        {
            if (long.TryParse(m.Groups[1].Value, out var v) && v > 10000)
                yield return v;
        }
    }
}
