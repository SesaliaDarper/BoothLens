using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BoothLens.Launcher;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var port = Environment.GetEnvironmentVariable("BOOTHLENS_PORT") ?? "5180";
        if (!int.TryParse(port, out var number) || number < 1 || number > 65535) { MessageBox.Show("BOOTHLENS_PORT 必须是有效端口。", "BoothLens"); return; }
        var scope = (Environment.GetEnvironmentVariable("BOOTHLENS_DATA") ?? "default") + "|" + port;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..20];
        using var mutex = new Mutex(true, @"Local\BoothLens.Launcher." + key, out var first);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BoothLens.Activate." + key);
        if (!first) { activate.Set(); return; }
        try { Application.Run(new LauncherWindow(port, activate, args.Contains("--smoke-test"))); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "BoothLens 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { mutex.ReleaseMutex(); }
    }
}

internal sealed class LauncherWindow : Form
{
    readonly string address;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    readonly Label state = new() { AutoSize = true, Text = "正在检查本地服务…", ForeColor = Color.FromArgb(82, 71, 160) };
    readonly Label detail = new() { AutoSize = false, Height = 56, Dock = DockStyle.Top };
    readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly Button open = new() { Text = "打开 BoothLens", AutoSize = true, Enabled = false };
    readonly Button retry = new() { Text = "重新启动", AutoSize = true };
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 4000 };
    readonly RegisteredWaitHandle activation;
    readonly bool smoke;
    readonly WebView2 web = new() { Dock = DockStyle.Fill, Visible = false };
    readonly Panel diagnostics = new() { Dock = DockStyle.Fill };
    bool webReady, webStarting;
    Process? child;
    string? launchToken;
    bool exiting, checking, online, starting;
    int activations;
    readonly List<string> testDownloads = new();
    string? testDownloadPath;

    internal LauncherWindow(string port, EventWaitHandle activate, bool smoke)
    {
        address = "http://127.0.0.1:" + port;
        this.smoke = smoke;
        Text = "BoothLens"; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 860); MinimumSize = new Size(940, 640);
        Font = new Font("Microsoft YaHei UI", 9); BackColor = Color.FromArgb(247, 247, 252);
        using (var iconStream = typeof(Program).Assembly.GetManifestResourceStream("BoothLens.Icon"))
            Icon = iconStream != null ? new Icon(iconStream) : SystemIcons.Application;
        var title = new Label { Text = "BoothLens", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), AutoSize = true };
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 90, Padding = new Padding(18, 10, 18, 0), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        header.Controls.Add(title); header.Controls.Add(state);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(14, 8, 0, 0) };
        var hide = new Button { Text = "收起到托盘", AutoSize = true };
        var quit = new Button { Text = "退出 BoothLens", AutoSize = true };
        var browser = new Button { Text = "浏览器打开", AutoSize = true };
        browser.Click += (_, _) => ExternalBrowser();
        actions.Controls.AddRange(new Control[] { open, browser, hide, retry, quit });
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 0, 18, 0) };
        body.Controls.Add(log); body.Controls.Add(detail);
        diagnostics.Controls.Add(body); diagnostics.Controls.Add(actions); diagnostics.Controls.Add(header);
        Controls.Add(diagnostics); Controls.Add(web);
        var bar = new MenuStrip { Dock = DockStyle.Top };
        bar.Items.Add("主界面", null, async (_, _) => await ShowApp());
        bar.Items.Add("重新加载", null, (_, _) => { if (webReady) web.Reload(); });
        bar.Items.Add("服务与日志", null, (_, _) => { web.Visible = false; diagnostics.Visible = true; diagnostics.BringToFront(); });
        bar.Items.Add("在浏览器打开", null, (_, _) => ExternalBrowser());
        bar.Items.Add("收起到托盘", null, (_, _) => Hide());
        bar.Items.Add("退出", null, async (_, _) => await ExitAsync(true));
        MainMenuStrip = bar; Controls.Add(bar);
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 BoothLens", null, (_, _) => OpenBrowser());
        menu.Items.Add("显示软件窗口", null, (_, _) => Reveal());
        menu.Items.Add("退出 BoothLens", null, async (_, _) => await ExitAsync(true));
        tray = new NotifyIcon { Icon = Icon, Text = "BoothLens", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => { if (online) OpenBrowser(); else Reveal(); };
        open.Click += (_, _) => OpenBrowser(); hide.Click += (_, _) => Hide();
        quit.Click += async (_, _) => await ExitAsync(true);
        retry.Click += async (_, _) => await EnsureStarted();
        FormClosing += (_, e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); tray.ShowBalloonTip(2500, "BoothLens 仍在运行", "双击托盘图标打开网页。要停止本程序启动的服务，请从托盘选择退出。", ToolTipIcon.Info); } };
        timer.Tick += async (_, _) => await RefreshHealth();
        activation = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { activations++; Reveal(); if (online) OpenBrowser(); }); }, null, -1, false);
        Shown += async (_, _) =>
        {
            await EnsureStarted();
            if (online) await ShowApp();
            timer.Start();
            if (smoke)
            {
                await Task.Delay(2500);
                var pageTitle = webReady ? await web.CoreWebView2.ExecuteScriptAsync("document.title") : "";
                var rendered = webReady ? await web.CoreWebView2.ExecuteScriptAsync("!!document.querySelector('#view-library') && !!document.querySelector('#theme-toggle')") : "false";
                if (webReady)
                {
                    foreach (var name in new[] { "BoothRelay.cs", "BoothRelay.zip" })
                    {
                        testDownloadPath = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("BOOTHLENS_LAUNCHER_TEST_REPORT")) ?? Path.GetTempPath(), name);
                        await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('a[download=\"" + name + "\"]').click()");
                        for (var i = 0; i < 100 && !testDownloads.Contains(testDownloadPath); i++) await Task.Delay(100);
                    }
                    testDownloadPath = null;
                }
                var screenshot = Environment.GetEnvironmentVariable("BOOTHLENS_DESKTOP_SCREENSHOT");
                if (webReady && !string.IsNullOrWhiteSpace(screenshot)) { using var image = File.Create(screenshot); await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image); }
                Close(); // A normal window close must leave this launcher and its backend alive in the tray.
                var hiddenToTray = !Visible;
                await Task.Delay(1500);
                var report = Environment.GetEnvironmentVariable("BOOTHLENS_LAUNCHER_TEST_REPORT");
                if (!string.IsNullOrWhiteSpace(report)) File.WriteAllText(report, JsonSerializer.Serialize(new { online, ownsServer = child != null && !child.HasExited, activations, hiddenToTray, address, webReady, pageTitle, rendered, downloads = testDownloads.Count }));
                await ExitAsync(false);
            }
        };
    }
    void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    void AppendLog(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() => { if (log.TextLength > 24000) log.Text = log.Text[^12000..]; log.AppendText(text + Environment.NewLine); });
    }
    async Task<bool> Health()
    {
        try
        {
            var r = await http.GetFromJsonAsync<JsonElement>(address + "/api/health");
            if (!r.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) return false;
            // Verify the BoothLens session endpoint as well as the basic health endpoint.
            var session = await http.GetFromJsonAsync<JsonElement>(address + "/api/session");
            return session.TryGetProperty("token", out var token) && token.GetString()?.Length == 64;
        }
        catch { return false; }
    }
    void SetState(bool ready)
    {
        online = ready; open.Enabled = ready;
        state.Text = ready ? "● 本地服务已就绪 · " + address : "● 本地服务未连接";
        detail.Text = ready ? child != null && !child.HasExited
            ? "网页关闭不影响服务。关闭此窗口会收起到托盘；从托盘退出时停止本启动器开启的服务。"
            : "已连接正在运行的 BoothLens，无需重复启动。退出本启动器不会关闭已有服务。"
            : "服务未就绪，请查看下方日志。点击“重新启动”重试；原有素材及索引保持不动。";
        tray.Text = ready ? "BoothLens · 运行中" : "BoothLens · 服务未连接";
    }
    async Task RefreshHealth()
    {
        if (checking || starting || exiting) return;
        checking = true;
        try { SetState(await Health()); }
        finally { checking = false; }
    }
    async Task EnsureStarted()
    {
        if (starting || exiting) return;
        starting = true; retry.Enabled = false;
        try
        {
            if (await Health()) { SetState(true); if (!smoke) OpenBrowser(); return; }
            if (child != null && !child.HasExited) { SetState(false); AppendLog("服务进程仍在启动，请稍后重试。不会重复启动。"); return; }
            child?.Dispose(); child = null;
            var root = AppContext.BaseDirectory;
            var exe = new[] { Path.Combine(root, "BoothLens.exe"), Path.Combine(root, "release", "BoothLens.exe") }.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("没有找到 BoothLens.exe。请将启动器放在完整发布包内，不要只移动启动器本身。");
            launchToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var info = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            info.Environment["BOOTHLENS_LAUNCHER_TOKEN"] = launchToken;
            child = new Process { StartInfo = info };
            child.OutputDataReceived += (_, e) => AppendLog(e.Data); child.ErrorDataReceived += (_, e) => AppendLog(e.Data);
            child.Start(); child.BeginOutputReadLine(); child.BeginErrorReadLine();
            state.Text = "正在启动本地服务…";
            var ready = false;
            for (var i = 0; i < 100 && !exiting; i++)
            {
                if (await Health()) { ready = true; break; }
                if (child.HasExited) break;
                await Task.Delay(300);
            }
            SetState(ready);
            if (ready) { if (!smoke) OpenBrowser(); }
            else if (!ready) AppendLog("启动未成功：请查看上述错误（端口占用、另一实例或发布文件缺失）。");
        }
        catch (Exception ex) { SetState(false); AppendLog(ex.Message); }
        finally { starting = false; retry.Enabled = true; }
    }
    void OpenBrowser() { if (!online) return; Reveal(); _ = ShowApp(); }
    void ExternalBrowser()
    {
        if (!online || smoke) return;
        OpenExternal(address);
    }
    void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return;
        try { if (!smoke) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "打开链接失败"); }
    }
    async Task ShowApp()
    {
        if (!online || exiting || webStarting) return;
        if (webReady) { diagnostics.Visible = false; web.Visible = true; web.BringToFront(); return; }
        webStarting = true;
        try
        {
            var data = Environment.GetEnvironmentVariable("BOOTHLENS_DATA");
            var profile = Path.Combine(string.IsNullOrWhiteSpace(data) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoothLens") : Path.GetFullPath(data), "desktop-profile");
            var env = await CoreWebView2Environment.CreateAsync(null, profile);
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.Settings.AreDevToolsEnabled = smoke;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.PermissionRequested += (_, e) => e.State = e.PermissionKind == CoreWebView2PermissionKind.MultipleAutomaticDownloads && Uri.TryCreate(e.Uri, UriKind.Absolute, out var origin) && origin.GetLeftPart(UriPartial.Authority) == address ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            core.NavigationStarting += (_, e) =>
            {
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var next) && next.GetLeftPart(UriPartial.Authority) == address) return;
                e.Cancel = true; OpenExternal(e.Uri);
            };
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            core.DownloadStarting += (_, e) =>
            {
                if (smoke && testDownloadPath != null)
                {
                    var target = testDownloadPath; if (File.Exists(target)) File.Delete(target); e.ResultFilePath = target; e.Handled = true;
                    e.DownloadOperation.StateChanged += (_, _) => { if (e.DownloadOperation.State == CoreWebView2DownloadState.Completed) testDownloads.Add(target); };
                    return;
                }
                using var save = new SaveFileDialog { FileName = Path.GetFileName(e.ResultFilePath), Title = "保存下载文件", OverwritePrompt = true };
                if (save.ShowDialog(this) == DialogResult.OK) { e.ResultFilePath = save.FileName; e.Handled = false; }
                else { e.Cancel = true; e.Handled = true; }
            };
            core.ProcessFailed += (_, e) => { webReady = false; web.Visible = false; diagnostics.Visible = true; AppendLog("桌面界面进程异常：" + e.ProcessFailedKind + "。请重启软件，或从浏览器继续使用。"); };
            var loaded = new TaskCompletionSource<bool>();
            void Navigated(object? sender, CoreWebView2NavigationCompletedEventArgs e) { loaded.TrySetResult(e.IsSuccess); }
            core.NavigationCompleted += Navigated;
            core.Navigate(address);
            var success = await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
            core.NavigationCompleted -= Navigated;
            if (!success) throw new IOException("桌面界面加载失败，请检查服务日志。");
            webReady = true; diagnostics.Visible = false; web.Visible = true; web.BringToFront();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            AppendLog("需要 Microsoft Edge WebView2 Runtime。安装后重新打开 BoothLens：https://developer.microsoft.com/microsoft-edge/webview2/");
            detail.Text = "缺少 WebView2 Runtime；可先点击“浏览器打开”继续使用。安装 WebView2 后即可使用桌面窗口。";
        }
        catch (Exception ex) { AppendLog("桌面界面：" + ex.Message); }
        finally { webStarting = false; }
    }
    async Task ExitAsync(bool confirm)
    {
        if (exiting) return;
        var owned = child != null && !child.HasExited;
        if (owned && confirm && MessageBox.Show("退出将停止本启动器开启的 BoothLens 服务。正在进行的扫描可能中断。\n仅关闭窗口则继续在托盘运行。", "退出 BoothLens", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        exiting = true; timer.Stop();
        if (owned)
        {
            try
            {
                var session = await http.GetFromJsonAsync<JsonElement>(address + "/api/session");
                using var request = new HttpRequestMessage(HttpMethod.Post, address + "/api/app/shutdown");
                request.Headers.Add("X-BoothLens-Token", session.GetProperty("token").GetString());
                request.Headers.Add("X-BoothLens-Launcher", launchToken);
                using var response = await http.SendAsync(request); response.EnsureSuccessStatusCode();
                await child!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                // Never force-kill a backend performing database or import work.
                if (child != null && !child.HasExited && !smoke)
                { exiting = false; timer.Start(); Reveal(); MessageBox.Show("服务尚未退出，已保留运行。\n" + ex.Message, "BoothLens"); return; }
            }
        }
        tray.Visible = false; activation.Unregister(null); tray.Dispose(); http.Dispose(); child?.Dispose(); Close();
    }
}
