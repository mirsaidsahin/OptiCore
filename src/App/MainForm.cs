using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using OptiCore.Engine;
using OptiCore.Engine.Core;

namespace OptiCore.App;

/// <summary>
/// The desktop shell: a WebView2 window hosting the UI from the local "ui" folder through a virtual host,
/// a message bridge to the engine, a tray icon, native dialogs and the in-game overlay.
/// </summary>
internal sealed class MainForm : Form
{
    private const string Host = "app.opticore";
    private readonly OptiCoreEngine _engine;
    private readonly string[] _args;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(9, 12, 18) };
    private readonly NotifyIcon _tray = new();
    private readonly Icon _icon;
    private OverlayForm? _overlay;
    private bool _exiting;
    private bool _startHidden;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainForm(OptiCoreEngine engine, string[] args, bool startHidden)
    {
        _engine = engine;
        _args = args;
        _startHidden = startHidden;
        using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("opticore.ico")!) _icon = new Icon(stream);

        Text = "OptiCore" + (engine.Options.DryRun ? " — simülasyon" : "") + (Engine.Platform.Elevation.IsElevated ? " (Yönetici)" : "");
        Icon = _icon;
        MinimumSize = new Size(1024, 680);
        Size = new Size(1440, 900);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(9, 12, 18);
        Controls.Add(_web);
        SetupTray();
        RegisterShellMethods();
        engine.Notices.Added += OnNotice;
    }

    protected override void SetVisibleCore(bool value)
    {
        // Started with --minimized: create the window handle but stay in the tray.
        if (_startHidden && !IsHandleCreated) { CreateHandle(); value = false; }
        base.SetVisibleCore(value);
    }

    protected override async void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBar(_engine.Settings.Current.Theme != "light");
        await InitWebView();
    }

    private async Task InitWebView()
    {
        try
        {
            string ui = ResolveUi();
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_engine.Store.Root, "webview"),
                new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection --autoplay-policy=no-user-gesture-required"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(Host, ui, CoreWebView2HostResourceAccessKind.DenyCors);
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationStarting += (_, a) =>
            {
                if (!a.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) { a.Cancel = true; OpenExternal(a.Uri); }
            };
            core.NewWindowRequested += (_, a) => { a.Handled = true; OpenExternal(a.Uri); };
            core.Navigate($"https://{Host}/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "OptiCore, Microsoft Edge WebView2 çalışma zamanına ihtiyaç duyar.\nhttps://developer.microsoft.com/microsoft-edge/webview2/",
                "OptiCore", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _exiting = true;
            Close();
        }
    }

    /// <summary>
    /// The UI folder: next to the exe in development builds; otherwise the copy embedded in OptiCore.exe,
    /// unpacked once per build into the data folder (WebView2 serves files from a real folder).
    /// </summary>
    private string ResolveUi()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "ui");
        if (File.Exists(Path.Combine(local, "index.html"))) return local;

        var assembly = typeof(MainForm).Assembly;
        string build = assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];
        string root = Path.Combine(_engine.Store.Root, "ui");
        string target = Path.Combine(root, build);
        if (!File.Exists(Path.Combine(target, ".complete")))
        {
            string temp = target + ".tmp";
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("ui/", StringComparison.Ordinal)))
            {
                string relative = name[3..].Replace('\\', '/');
                string path = Path.GetFullPath(Path.Combine(temp, relative));
                if (!path.StartsWith(Path.GetFullPath(temp), StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = assembly.GetManifestResourceStream(name)!;
                using var output = File.Create(path);
                input.CopyTo(output);
            }
            File.WriteAllText(Path.Combine(temp, ".complete"), OptiCoreEngine.Version);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(temp, target);
        }
        // Remove the UI of older builds.
        foreach (var old in Directory.EnumerateDirectories(root).Where(d => !d.Equals(target, StringComparison.OrdinalIgnoreCase)))
            try { Directory.Delete(old, true); } catch { /* in use by another instance */ }
        return target;
    }

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only our own page may talk to the engine.
        if (!e.Source.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) return;
        string request;
        try { request = e.TryGetWebMessageAsString(); } catch { return; }
        string response = await _engine.Rpc.HandleAsync(request);
        try { _web.CoreWebView2?.PostWebMessageAsString(response); } catch { /* window closing */ }
    }

    // ------------------------------------------------------------------ native capabilities for the UI
    private T OnUi<T>(Func<T> work) => InvokeRequired ? (T)Invoke(work) : work();

    private void RegisterShellMethods()
    {
        var rpc = _engine.Rpc;
        rpc.On("shell.relaunchElevated", _ => OnUi(() =>
        {
            if (!Program.RelaunchElevated(_args)) return false;
            _exiting = true;
            BeginInvoke(Close);
            return true;
        }));
        rpc.On("shell.pickFolder", _ => OnUi<string?>(() =>
        {
            using var dlg = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "OptiCore" };
            return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
        }));
        rpc.On("shell.pickFile", c => OnUi<string?>(() =>
        {
            using var dlg = new OpenFileDialog { Filter = c.Str("filter", "*.*|*.*") };
            return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
        }));
        rpc.On("shell.saveText", c =>
        {
            string name = Path.GetFileName(c.Str("name")), text = c.Str("text");
            return OnUi(() =>
            {
                using var dlg = new SaveFileDialog { FileName = name, Filter = Path.GetExtension(name) is { Length: > 0 } ext ? $"*{ext}|*{ext}|*.*|*.*" : "*.*|*.*" };
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                File.WriteAllText(dlg.FileName, text, new System.Text.UTF8Encoding(false));
                return true;
            });
        });
        rpc.Do("shell.setTheme", c => { bool dark = c.Str("theme", "dark") != "light"; OnUi(() => { ApplyTitleBar(dark); return true; }); });
        rpc.On("shell.overlay", c => OnUi(() =>
        {
            bool on = c.Bool("on", _overlay is null);
            if (on && _overlay is null) { _overlay = new OverlayForm(_engine); _overlay.FormClosed += (_, _) => _overlay = null; _overlay.Show(); }
            else if (!on) _overlay?.Close();
            return _overlay is not null;
        }));
    }

    private void ApplyTitleBar(bool dark)
    {
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref value, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        int backdrop = 2; // DWMSBT_MAINWINDOW (Mica) where available
        DwmSetWindowAttribute(Handle, 38, ref backdrop, sizeof(int));
        BackColor = dark ? Color.FromArgb(9, 12, 18) : Color.FromArgb(242, 244, 249);
        _web.DefaultBackgroundColor = BackColor;
    }

    // ------------------------------------------------------------------ tray
    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("OptiCore'u aç / Open", null, (_, _) => BringToFrontFromTray());
        menu.Items.Add("Şu an neden yavaş? / Why is it slow?", null, (_, _) => { BringToFrontFromTray(); Navigate("insights/live"); });
        menu.Items.Add("Oyun katmanı / Game overlay", null, (_, _) => _engine.Rpc.HandleAsync("{\"id\":0,\"method\":\"shell.overlay\",\"params\":{}}"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış / Exit", null, (_, _) => { _exiting = true; Close(); });
        _tray.Icon = _icon;
        _tray.Text = "OptiCore";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => BringToFrontFromTray();
        _tray.BalloonTipClicked += (_, _) => { BringToFrontFromTray(); if (_tray.Tag is string route) Navigate(route); };
    }

    private void Navigate(string route) => _web.CoreWebView2?.ExecuteScriptAsync($"location.hash = '#/{route.Replace("'", "")}'");

    public void BringToFrontFromTray()
    {
        _startHidden = false;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        TopMost = true;
        TopMost = false;
    }

    private void OnNotice(Notice n)
    {
        if (n.Level is not (NoticeLevel.Warn or NoticeLevel.Critical) && Visible) return;
        try
        {
            BeginInvoke(() =>
            {
                _tray.Tag = n.Route;
                _tray.ShowBalloonTip(6000, n.Title.ToString(), n.Body.ToString(), n.Level is NoticeLevel.Critical or NoticeLevel.Warn ? ToolTipIcon.Warning : ToolTipIcon.Info);
            });
        }
        catch { /* window not ready */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing && _engine.Settings.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_engine.Store.Load("ui/tray-hint.json", () => false))
            {
                _tray.ShowBalloonTip(5000, "OptiCore", "OptiCore arka planda çalışmaya devam ediyor. Tamamen kapatmak için bildirim alanındaki simgeye sağ tıklayın.", ToolTipIcon.Info);
                _engine.Store.Save("ui/tray-hint.json", true);
            }
            return;
        }
        _tray.Visible = false;
        _overlay?.Close();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.Notices.Added -= OnNotice;
            _tray.Dispose();
            _web.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
