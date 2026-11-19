using System.Text.Json;
using System.Text.RegularExpressions;
using BoothLens.Core.Booth;
using BoothLens.Core.Models;

namespace BoothLens.Core.Library;

public sealed record MatchItem(long Id, string Title, string Norm, HashSet<string> Bigrams);

public sealed class Catalog(BoothClient remote, ItemStore store, AppPaths paths)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly Regex ItemPath = new(@"^/(?:[a-z]{2}(?:-[a-z]{2})?/)?items/([0-9]+)/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] Extensions = [".jpg", ".png", ".webp", ".gif"];

    public static bool BoothUrl(Uri u) => u.Scheme == "https" && u.IsDefaultPort && u.UserInfo.Length == 0
        && (u.Host == "booth.pm" || u.Host.EndsWith(".booth.pm", StringComparison.OrdinalIgnoreCase));

    public static long ParseInput(string? value)
    {
        var s = value?.Trim() ?? "";
        if (Regex.IsMatch(s, @"^[0-9]+$") && long.TryParse(s, out var id) && id > 0) return id;
        if (Uri.TryCreate(s, UriKind.Absolute, out var url) && BoothUrl(url))
        {
            var m = ItemPath.Match(url.AbsolutePath);
            if (m.Success && long.TryParse(m.Groups[1].Value, out id) && id > 0) return id;
        }
        throw new ArgumentException("请输入 BOOTH 商品编号或 HTTPS 商品链接。");
    }

    public string? Cover(long id)
    {
        foreach (var ext in Extensions)
        {
            var file = Path.Combine(paths.ImageDir, $"{id}{ext}");
            if (File.Exists(file) && new FileInfo(file).Length > 0) return file;
        }
        return null;
    }

    public async Task<Item> Add(string? input, CancellationToken ct, bool fetchCover = true)
    {
        var id = ParseInput(input);
        await gate.WaitAsync(ct);
        try
        {
            var cached = store.Get(id);
            var cover = Cover(id);
            if (cached != null && (!fetchCover || (cover != null && cached.FetchState != "metadata_only") || cached.FetchState == "no_image")) return cached;
            var item = ItemParser.ParseItem(await remote.GetItemJsonAsync(id, ct));
            if (item.Id != id || string.IsNullOrWhiteSpace(item.Title)) throw new BoothException("BOOTH 返回的资料与指定商品不符。");
            if (!Uri.TryCreate(item.CanonicalUrl, UriKind.Absolute, out var uri) || !BoothUrl(uri)) item.CanonicalUrl = item.Url;
            if (fetchCover && cover == null && !string.IsNullOrWhiteSpace(item.RemoteImage))
            {
                var ext = Path.GetExtension(new Uri(item.RemoteImage).AbsolutePath).ToLowerInvariant();
                if (ext == ".jpeg") ext = ".jpg";
                if (!Extensions.Contains(ext)) ext = ".jpg";
                var dest = Path.Combine(paths.ImageDir, $"{id}{ext}");
                try { await remote.DownloadToFileAsync(item.RemoteImage, dest, ct); cover = dest; }
                catch (Exception ex) when (ex is HttpRequestException or BoothException or IOException or UriFormatException)
                {
                    item.FetchState = "image_error";
                    item.FetchError = "主图下载失败，可再次添加同一编号重试。";
                }
            }
            else if (cover == null) item.FetchState = fetchCover ? "no_image" : "metadata_only";
            item.Thumb = cover == null ? null : Path.GetFileName(cover);
            store.Upsert(item);
            return store.Get(id)!;
        }
        finally { gate.Release(); }
    }

    public async Task<Item> Description(long id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var item = store.Get(id) ?? throw new KeyNotFoundException("资源尚未入库。");
            if (id < 0 || item.DescFetchedAt != null) return item;
            var details = ItemParser.ParseDetail(await remote.GetItemHtmlAsync(id, ct));
            if (details.Text.Length > 0) store.SetFullDesc(id, details.Text, JsonSerializer.Serialize(details.Sections));
            return store.Get(id)!;
        }
        finally { gate.Release(); }
    }

    public async Task<List<SearchHit>> Search(string? value, CancellationToken ct)
    {
        var query = value?.Trim() ?? "";
        if (query.Length is 0 or > 120 || query is "." or "..") throw new ArgumentException("搜索词应为 1–120 个字符。");
        var hits = ItemParser.ParseSearch(await remote.SearchHtmlAsync(query, 1, ct));
        foreach (var hit in hits) { hit.Thumb = null; hit.AlreadyLocal = store.Exists(hit.Id); }
        return hits;
    }

    public async Task<Item> AddCustom(string? title, string? sourceUrl, string? imageBase64, CancellationToken ct)
    {
        title = title?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 160) throw new ArgumentException("自定义标题应为 1–160 个字符。");
        if (!string.IsNullOrWhiteSpace(sourceUrl) && (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https") || source.UserInfo.Length != 0))
            throw new ArgumentException("来源链接应为 HTTP 或 HTTPS。");
        byte[]? image = null;
        string? ext = null;
        if (!string.IsNullOrEmpty(imageBase64))
        {
            if (imageBase64.Length > 12_000_000) throw new ArgumentException("封面大小上限为 8 MB。");
            image = Convert.FromBase64String(imageBase64);
            if (image.Length > 8 * 1024 * 1024) throw new ArgumentException("封面大小上限为 8 MB。");
            ext = Detect(image) ?? throw new ArgumentException("请选择 JPG、PNG、WebP 或 GIF 图片。");
        }
        await gate.WaitAsync(ct);
        try
        {
            long id;
            do { id = -Random.Shared.NextInt64(1, 9_000_000_000_000_000L); } while (store.Exists(id) || Cover(id) != null);
            var item = new Item { Id = id, Title = title, CanonicalUrl = string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl, FetchedAt = DateTimeOffset.UtcNow.ToString("O"), FetchState = image == null ? "no_image" : "ok" };
            if (image != null)
            {
                item.Thumb = $"{id}{ext}";
                var dest = Path.Combine(paths.ImageDir, item.Thumb);
                var tmp = dest + ".tmp";
                try { await File.WriteAllBytesAsync(tmp, image, ct); File.Move(tmp, dest); }
                finally { File.Delete(tmp); }
            }
            store.Upsert(item);
            return store.Get(id)!;
        }
        finally { gate.Release(); }
    }

    private static string? Detect(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return ".jpg";
        if (b.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (b.StartsWith("GIF87a"u8) || b.StartsWith("GIF89a"u8)) return ".gif";
        if (b.Length >= 12 && b.StartsWith("RIFF"u8) && b[8..].StartsWith("WEBP"u8)) return ".webp";
        return null;
    }
}
