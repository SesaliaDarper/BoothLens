using System.Net;
using System.Security.Cryptography;
using BoothLens.Core;
using BoothLens.Core.Booth;
using BoothLens.Core.Data;
using BoothLens.Core.Library;

var data = Environment.GetEnvironmentVariable("BOOTHLENS_DATA");
var paths = AppPaths.CreateDefault(string.IsNullOrWhiteSpace(data) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoothLens") : data);
var portText = Environment.GetEnvironmentVariable("BOOTHLENS_PORT");
var port = string.IsNullOrWhiteSpace(portText) ? 5180 : int.Parse(portText);
var address = $"http://127.0.0.1:{port}";
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args.Where(a => a != "--open").ToArray(), ContentRootPath = AppContext.BaseDirectory, WebRootPath = "wwwroot" });
builder.WebHost.ConfigureKestrel(options => { options.Listen(IPAddress.Loopback, port); options.Limits.MaxRequestBodySize = 12_000_000; });
var dataLock = new FileStream(Path.Combine(paths.Root, ".instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
using var instanceLock = dataLock;
var db = new Db(paths.DbPath);
db.EnsureSchema();
var store = new ItemStore(db);
using var booth = new BoothClient { CookieHeader = Environment.GetEnvironmentVariable("BOOTHLENS_BOOTH_COOKIE") };
var catalog = new Catalog(booth, store, paths);
var local = new ContainerLibrary(db, store, catalog, booth);
var importer = new Importer(paths, store);
var editors = new UnityEditors();
var app = builder.Build();
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Host.Host != "127.0.0.1" && !ctx.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
    { ctx.Response.StatusCode = 400; return; }
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' blob: data:; style-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers.CacheControl = "no-store";
    if ((HttpMethods.IsPost(ctx.Request.Method) || HttpMethods.IsDelete(ctx.Request.Method)) &&
        (!string.Equals(ctx.Request.Headers["X-BoothLens-Token"].ToString(), token, StringComparison.Ordinal)
        || (ctx.Request.Headers.TryGetValue("Origin", out var origin) && origin != address && origin != $"http://localhost:{port}")))
    { await Results.Json(new { error = "页面会话已失效，请刷新后重试。" }, statusCode: 403).ExecuteAsync(ctx); return; }
    try { await next(ctx); }
    catch (Exception ex) when (!ctx.Response.HasStarted && !ctx.RequestAborted.IsCancellationRequested)
    {
        (int status, string message) = ex switch
        {
            BadHttpRequestException e => (e.StatusCode, "请求格式不正确或内容过大。"),
            KeyNotFoundException => (404, ex.Message),
            BoothNotFoundException => (404, ex.Message),
            BoothException => (502, ex.Message),
            HttpRequestException => (502, "访问 BOOTH 失败，请检查网络后重试。"),
            System.Text.Json.JsonException => (502, "BOOTH 返回了无法读取的商品数据。"),
            OperationCanceledException => (504, "请求超时，请重试。"),
            ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException => (400, ex.Message),
            _ => (500, "操作失败，请查看启动窗口中的日志。")
        };
        if (status == 500) app.Logger.LogError(ex, "BoothLens operation failed");
        await Results.Json(new { error = message }, statusCode: status).ExecuteAsync(ctx);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();

byte[] RelayScript()
{
    using var stream = typeof(Program).Assembly.GetManifestResourceStream("BoothLens.Unity.BoothRelay.cs")
        ?? throw new InvalidOperationException("发布程序中缺少 Unity 桥接脚本，请重新安装程序。");
    using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
}
app.MapGet("/unity/BoothRelay.cs", () => Results.File(RelayScript(), "application/octet-stream", "BoothRelay.cs"));
app.MapGet("/unity/BoothRelay.zip", () =>
{
    using var output = new MemoryStream();
    using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
        var entry = zip.CreateEntry("Assets/Editor/BoothRelay.cs");
        using var target = entry.Open(); target.Write(RelayScript());
    }
    return Results.File(output.ToArray(), "application/zip", "BoothRelay.zip");
});
app.MapGet("/api/health", () => new { ok = true });
app.MapGet("/api/status", () => new { items = store.Count(), requests = booth.RequestCount, imageRequests = booth.ImageRequestCount });
app.MapGet("/api/session", () => new { token });
app.MapGet("/api/items", (string? q, int? page, int? pageSize) =>
{
    var (items, total) = store.Search(q, ClampPage(page), Math.Clamp(pageSize ?? 24, 1, 100), null, false);
    return new { items, total };
});
app.MapGet("/api/items/{id:long}", (long id) => new { item = store.Get(id) ?? throw new KeyNotFoundException("资源尚未入库。"), files = store.ListFilesForItem(id) });
app.MapPost("/api/items", (AddRequest body, CancellationToken ct) => catalog.Add(body.Input, ct));
app.MapPost("/api/custom", (CustomRequest body, CancellationToken ct) => catalog.AddCustom(body.Title, body.SourceUrl, body.ImageBase64, ct));
app.MapPost("/api/items/{id:long}/description", (long id, CancellationToken ct) => catalog.Description(id, ct));
app.MapGet("/api/search/booth", (string? q, CancellationToken ct) => catalog.Search(q, ct));
app.MapGet("/img/{id:long}", async (long id, HttpContext ctx) =>
{
    var item = store.Get(id);
    if (item == null) return Results.NotFound();
    // Migrate old metadata-only bindings lazily, without rescanning their folders.
    if (id > 0 && item.FetchState == "metadata_only" && catalog.Cover(id) == null)
        await catalog.Add(id.ToString(), ctx.RequestAborted);
    if (catalog.Cover(id) is not { } path) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "private, max-age=86400";
    var contentType = Path.GetExtension(path) switch { ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/jpeg" };
    return Results.File(path, contentType, lastModified: File.GetLastWriteTimeUtc(path));
});
app.MapGet("/api/files", (string? status, int? page, int? pageSize, string? q) => local.List(status, ClampPage(page), Math.Clamp(pageSize ?? 24, 1, 100), q));
app.MapGet("/api/resources", (string? status, int? page, int? pageSize, string? q) => local.List(status, ClampPage(page), Math.Clamp(pageSize ?? 24, 1, 100), q));
app.MapGet("/api/resources/detail", (string path) => local.Get(path) ?? throw new KeyNotFoundException("资源容器不存在。"));
app.MapGet("/api/roots", () => local.ListRoots());
app.MapGet("/api/structure", (string root) => local.Structure(root));
app.MapGet("/api/resources/boundaries", (string? root) => local.GetBoundaries(root));
app.MapPost("/api/resources/boundary", (BoundaryRequest body) => local.ConfigureBoundary(body.Path ?? "", body.Kind ?? "auto"));
app.MapPost("/api/scan/repair", (ScanRequest body) => local.Repair(body.Root?.Trim() ?? ""));
app.MapPost("/api/scan/preview", (ScanRequest body) => local.Preview(body.Root?.Trim() ?? "", body.Mode));
app.MapPost("/api/scan", (ScanRequest body) => local.Start(body.Root?.Trim() ?? "", body.Mode, body.Online ?? true));
app.MapGet("/api/scan/status", () => local.GetStatus());
app.MapPost("/api/scan/cancel", () => { local.Cancel(); return local.GetStatus(); });
app.MapPost("/api/resources/confirm", async (ConfirmRequest body, CancellationToken ct) =>
{
    if (local.Get(body.Path ?? "") == null) throw new KeyNotFoundException("资源容器不存在。");
    if (body.Id > 0) await catalog.Add(body.Id.ToString(), ct);
    return local.Confirm(body.Path ?? "", body.Id);
});
app.MapPost("/api/resources/retry", (RetryRequest body) => local.Retry(body.Path ?? ""));
app.MapPost("/api/resources/unbind", (RetryRequest body) => local.Unbind(body.Path ?? ""));
app.MapPost("/api/resources/import", (ResourceImportRequest body) =>
{
    var project = editors.Resolve(body.ProjectId);
    var source = local.PrepareImport(body.Path ?? "", body.MemberPath);
    return importer.Import(source, body.Mode, project.ProjectPath);
});
app.MapPost("/api/link", async (LinkRequest body, CancellationToken ct) =>
{
    var path = body.Path ?? "";
    if (store.GetLocalFile(path) == null) throw new KeyNotFoundException("请先扫描该本地文件所在目录。");
    var input = body.Input?.Trim();
    var item = long.TryParse(input, out var id) && id < 0
        ? store.Get(id) ?? throw new KeyNotFoundException("该自定义资源尚未入库。")
        : await catalog.Add(input, ct);
    if (local.Get(path) != null) local.Confirm(path, item.Id);
    else
    {
        store.SetFileLink(path, item.Id, 1, "manual", "auto");
        store.AddUserLink(path, item.Id, null);
    }
    return item;
});
app.MapGet("/api/settings", () => new { hasCookie = !string.IsNullOrEmpty(booth.CookieHeader), unityTargetMode = "open-editors" });
app.MapPost("/api/settings", (SettingsRequest body) =>
{
    if (body.CookieHeader != null)
    {
        var cookie = body.CookieHeader.Trim();
        if (cookie.Length > 16000 || cookie.Any(c => c < 32 || c > 126)) throw new ArgumentException("Cookie 请求头格式不正确。");
        booth.CookieHeader = cookie.Length == 0 ? null : cookie;
    }
    return new { hasCookie = !string.IsNullOrEmpty(booth.CookieHeader), unityTargetMode = "open-editors" };
});
app.MapPost("/api/import", (ImportRequest body) => importer.Import(body.Path ?? "", body.Mode, editors.Resolve(body.ProjectId).ProjectPath));
app.MapGet("/api/unity/projects", () => new { projects = editors.List(), message = "仅显示已打开且安装新版桥接脚本的 Unity 编辑器；不使用设置中保存的历史工程路径。" });
app.MapGet("/api/jobs", () =>
{
    foreach (var project in editors.List()) importer.Poll(project.ProjectPath);
    return store.ListJobs();
});
app.MapPost("/api/resources/cover", (LocalCoverRequest body) =>
{
    if (string.IsNullOrWhiteSpace(body.ImageBase64)) throw new ArgumentException("请选择封面图片。");
    if (body.ImageBase64.Length > 12_000_000) throw new ArgumentException("封面大小上限为 8 MB。");
    return local.SetLocalCover(body.Path ?? "", Convert.FromBase64String(body.ImageBase64));
});
app.MapDelete("/api/resources/cover", (string path) => local.ClearLocalCover(path));
app.MapGet("/api/resources/cover", (string path) =>
{
    var image = local.LocalCover(path) ?? throw new KeyNotFoundException("本地配图已移除或尚未发现。");
    var type = Path.GetExtension(image).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/jpeg" };
    return Results.File(image, type, lastModified: File.GetLastWriteTimeUtc(image));
});
app.MapPost("/api/resources/reveal", (RevealRequest body) =>
{
    ResourceLocations.Reveal(body.Path ?? "", db, store, local);
    return new { ok = true };
});
if (args.Contains("--open"))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception ex) { app.Logger.LogWarning("Open browser: {Message}", ex.Message); }
    });
}
Console.WriteLine($"BoothLens: {address}\nData: {paths.Root}\nCookie is session-only; it is not saved to disk.");
app.Run();

static int ClampPage(int? page) => Math.Clamp(page ?? 1, 1, 10000);
record AddRequest(string? Input);
record CustomRequest(string? Title, string? SourceUrl, string? ImageBase64);
record ScanRequest(string? Root, string? Mode, bool? Online);
record ConfirmRequest(string? Path, long Id);
record RetryRequest(string? Path);
record LocalCoverRequest(string? Path, string? ImageBase64);
record BoundaryRequest(string? Path, string? Kind);
record ResourceImportRequest(string? Path, string? MemberPath, string? Mode, string? ProjectId);
record RevealRequest(string? Path);
record LinkRequest(string? Path, string? Input);
record SettingsRequest(string? CookieHeader);
record ImportRequest(string? Path, string? Mode, string? ProjectId);
