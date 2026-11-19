using System.Net;

namespace BoothLens.Core.Booth;

public class BoothException : Exception
{
    public BoothException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class BoothNotFoundException : BoothException
{
    public long ItemId { get; }
    public BoothNotFoundException(long id) : base($"BOOTH 商品 {id} 不存在（HTTP 404）") => ItemId = id;
}

public sealed class BoothAuthException : BoothException
{
    public BoothAuthException(string message) : base(message) { }
}

/// <summary>
/// BOOTH 抓取端。全局串行 + 最小间隔节流：并发只会让自己被封。
/// 实测结论：.json 接口无需 Referer；pximg 图片也无需 Referer。
/// </summary>
public sealed class BoothClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    private long _requests;
    private long _imageRequests;
    public long RequestCount => Interlocked.Read(ref _requests);
    public long ImageRequestCount => Interlocked.Read(ref _imageRequests);
    public int ThrottleMs { get; set; } = 1200;
    public string? CookieHeader { get; set; }

    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    public BoothClient() : this(CreateHandler()) { }

    public BoothClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ja,en;q=0.8,zh-CN;q=0.6");
    }

    private static HttpMessageHandler CreateHandler()
    {
        return new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip
                                   | DecompressionMethods.Deflate
                                   | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = false,
            UseCookies = false,
            MaxConnectionsPerServer = 4
        };
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_last != DateTimeOffset.MinValue && ThrottleMs > 0)
            {
                var elapsed = (DateTimeOffset.UtcNow - _last).TotalMilliseconds;
                var wait = ThrottleMs - elapsed;
                if (wait > 0) await Task.Delay((int)wait, ct).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _requests);
            if (req.RequestUri?.Host == "booth.pximg.net") Interlocked.Increment(ref _imageRequests);
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            _last = DateTimeOffset.UtcNow;
            return resp;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ApplyCookie(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(CookieHeader))
            req.Headers.TryAddWithoutValidation("Cookie", CookieHeader);
    }

    public async Task<string> GetItemJsonAsync(long id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://booth.pm/ja/items/{id}.json");
        ApplyCookie(req);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

        using var resp = await SendAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotFound) throw new BoothNotFoundException(id);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new BoothAuthException($"BOOTH 对商品 {id} 返回 {(int)resp.StatusCode}，该商品可能需要登录 cookie。");

        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> GetItemHtmlAsync(long id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://booth.pm/ja/items/{id}");
        ApplyCookie(req);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new BoothNotFoundException(id);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new BoothAuthException($"BOOTH 对商品 {id} 的页面返回 {(int)resp.StatusCode}，可能需要登录 cookie。");

        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>按关键词搜索 BOOTH 商品（日文关键词命中率最高）。</summary>
    public async Task<string> SearchHtmlAsync(string query, int page, CancellationToken ct = default)
    {
        var url = $"https://booth.pm/ja/search/{Uri.EscapeDataString(query)}?page={Math.Max(1, page)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCookie(req);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>整个商品只下一次图。写到 .tmp 再原子改名，避免半截文件被当成有效缓存。</summary>
    public async Task<long> DownloadToFileAsync(string url, string destPath, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "booth.pximg.net" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new BoothException("主图地址不是 BOOTH 图片 CDN 地址。");
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);

        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > 16 * 1024 * 1024 || resp.Content.Headers.ContentType?.MediaType is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif"))
            throw new BoothException("BOOTH 主图格式不支持或超过 16 MB。");

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = destPath + ".tmp";
        long total = 0;

        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buf = new byte[81920];
                int n;
                while ((n = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > 16 * 1024 * 1024) throw new BoothException("BOOTH 主图超过 16 MB。");
                    await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            if (total == 0) throw new BoothException("BOOTH 返回了空图片。");
            File.Move(tmp, destPath, overwrite: true);
            return total;
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
