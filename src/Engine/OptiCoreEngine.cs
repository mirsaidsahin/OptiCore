using System.Diagnostics;
using System.Reflection;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Modules;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine;

public sealed class EngineOptions
{
    /// <summary>Where OptiCore keeps its state. Defaults to %LOCALAPPDATA%\OptiCore.</summary>
    public string? DataDir { get; set; }
    /// <summary>Simulation mode: every scan is real, but nothing on the system is modified.</summary>
    public bool DryRun { get; set; }
    /// <summary>desktop (WebView2 shell) or browser (development server).</summary>
    public string Host { get; set; } = "desktop";
}

/// <summary>Composition root: builds every service once and exposes them through one RPC router.</summary>
public sealed partial class OptiCoreEngine : IDisposable
{
    public EngineOptions Options { get; }
    public RpcRouter Rpc { get; } = new();
    public JobManager Jobs { get; } = new();
    public DataStore Store { get; }
    public SettingsService Settings { get; }
    public NotificationCenter Notices { get; }
    public ChangeJournal Journal { get; }
    public MetricsSampler Metrics { get; }
    public ProcessSampler ProcessSampler { get; }
    public ProcessService Processes { get; }
    public SystemInfoService SystemInfo { get; }
    public InsightsEngine Insights { get; }
    public CleanupService Cleanup { get; }
    public StartupService Startup { get; }
    public TweakService Tweaks { get; }
    public BackupService Backup { get; }
    public SecurityService Security { get; }
    public PrivacyService Privacy { get; }
    public StorageService Storage { get; }
    public AppsService Apps { get; }
    public DriverService Drivers { get; }
    public NetworkService Network { get; }
    public DiagnosticsService Diagnostics { get; }
    public GamingService Gaming { get; }
    public OverclockService Overclock { get; }
    public BenchmarkService Benchmark { get; }
    public AutomationService Automation { get; }
    public ToolsService Tools { get; }
    /// <summary>Full path of OptiCore.exe when running in the desktop shell (used for logon tasks and autostart).</summary>
    public string? AppExecutable { get; set; }

    public static string Version { get; } =
        typeof(OptiCoreEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";

    public OptiCoreEngine(EngineOptions options)
    {
        Options = options;
        Store = new DataStore(options.DataDir ?? Path.Combine(Paths.Local, "OptiCore"));
        Log.Init(Store.DirOf("logs"));
        Log.Info($"engine starting v{Version} elevated={Elevation.IsElevated} dryRun={options.DryRun} host={options.Host}");

        Settings = new SettingsService(Store);
        Loc.Default = Settings.Current.Language == "en" ? "en" : "tr";
        Notices = new NotificationCenter(Store);
        Journal = new ChangeJournal(Store, options.DryRun);
        Metrics = new MetricsSampler(Store);
        ProcessSampler = new ProcessSampler(Metrics);
        Processes = new ProcessService(ProcessSampler, Journal, Store, Settings, Notices);
        SystemInfo = new SystemInfoService();
        Insights = new InsightsEngine(Metrics, ProcessSampler, SystemInfo, Settings, Store, Jobs);

        Cleanup = new CleanupService(Journal, Jobs, Store);
        Startup = new StartupService(Journal, Store, Notices, Settings, ProcessSampler);
        Tweaks = new TweakService(Journal, Store);

        Backup = new BackupService(Journal, Tweaks, Settings, Startup);
        Security = new SecurityService(ProcessSampler, Startup, SystemInfo);
        Privacy = new PrivacyService(Journal);
        Storage = new StorageService(SystemInfo, Store, Jobs, Journal);

        Apps = new AppsService(Journal, Jobs);
        Drivers = new DriverService(Jobs, Store);
        Network = new NetworkService(Jobs, Journal, ProcessSampler);
        Gaming = new GamingService(Metrics, ProcessSampler, Journal, Store, Notices);
        Gaming.Register(Rpc);
        Benchmark = new BenchmarkService(Jobs, Store, Metrics, SystemInfo);
        Benchmark.Register(Rpc);
        Overclock = new OverclockService(SystemInfo, Apps, Journal);
        Overclock.Register(Rpc);
        Insights.AddSource("overclock", Overclock.Findings);
        Diagnostics = new DiagnosticsService(Jobs, Journal);
        Diagnostics.Register(Rpc);
        Insights.AddSource("diagnostics", Diagnostics.Findings, slow: true);
        Apps.Register(Rpc);
        Drivers.Register(Rpc);
        Network.Register(Rpc);
        Insights.AddSource("apps", Apps.Findings, slow: true);
        Insights.AddSource("drivers", Drivers.Findings, slow: true);

        Cleanup.Register(Rpc);
        Startup.Register(Rpc);
        Tweaks.Register(Rpc);
        Backup.Register(Rpc);
        Security.Register(Rpc);
        Privacy.Register(Rpc);
        Storage.Register(Rpc);

        Automation = new AutomationService(Store, Insights, Cleanup, Startup, Tweaks, Notices, Metrics, Journal);
        Automation.Register(Rpc, () => AppExecutable);
        Tools = new ToolsService(Journal, Store, Apps, Startup, Drivers, Tweaks, SystemInfo, Jobs);
        Tools.Register(Rpc);

        Insights.AddSource("security", Security.Findings, slow: true);
        Insights.AddSource("privacy", Privacy.Findings, slow: true);
        Insights.AddSource("storage", Storage.Findings);

        // Findings: the source name is also the prefix of its finding ids.
        Insights.AddSource("cleanup", Cleanup.Findings);
        Insights.AddSource("tweaks", Tweaks.Findings);
        Insights.AddSource("startup", Startup.Findings, slow: true);
        Insights.AddBaselineProbe("startupEnabled", () => Startup.List().Count(e => e.Enabled && e.Kind != "service"));
        Insights.AddBaselineProbe("junkBytes", () => Cleanup.LastScan?.RecommendedBytes ?? 0);

        Jobs.Register(Rpc);
        Insights.Register(Rpc);
        Settings.Register(Rpc);
        Notices.Register(Rpc);
        Journal.Register(Rpc);
        Metrics.Register(Rpc);
        Processes.Register(Rpc);
        SystemInfo.Register(Rpc);
        RegisterShell();
        BuildModules();
    }

    /// <summary>Modules added in other files hook in here.</summary>
    partial void BuildModules();
    partial void StartModules();

    public void Start()
    {
        Metrics.Start();
        Processes.Start();
        Startup.Start();
        Storage.Start();
        Gaming.Start();
        Automation.Start();
        StartModules();
        // The dashboard starts with the quick checks; the slow ones (security, drivers, event logs) follow in the background.
        if (Options.Host == "desktop")
            _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ =>
            {
                try { Jobs.Start("insights.analyze", Loc.T("Sistem analizi", "System analysis"), job => Insights.Analyze(true, job)); }
                catch (Exception e) { Log.Warn("startup analysis", e); }
            });
    }

    private void RegisterShell()
    {
        Rpc.On("app.info", _ => new
        {
            version = Version,
            elevated = Elevation.IsElevated,
            dryRun = Options.DryRun,
            host = Options.Host,
            dataDir = Store.Root,
            machine = Environment.MachineName,
            user = Environment.UserName,
            methods = Rpc.Methods.Count,
        });

        // Only web links and Windows' own settings pages may be opened from the UI.
        Rpc.Do("shell.open", c =>
        {
            string url = c.Str("url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "ms-settings" or "windowsdefender" or "ms-windows-store"))
                throw RpcException.Denied("Unsupported link");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        });

        Rpc.Do("shell.reveal", c =>
        {
            string path = c.Str("path");
            if (File.Exists(path)) Process.Start(new ProcessStartInfo(Path.Combine(Paths.Windows, "explorer.exe"), $"/select,\"{path}\"") { UseShellExecute = false });
            else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(Path.Combine(Paths.Windows, "explorer.exe"), $"\"{path}\"") { UseShellExecute = false });
            else throw RpcException.NotFound(Loc.S("Yol bulunamadı.", "The path no longer exists."));
        });

        const string runKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
        Rpc.On("app.autostart", _ => Reg.GetStr(runKey, "OptiCore") is { Length: > 0 });
        Rpc.On("app.setAutostart", c =>
        {
            string exe = AppExecutable ?? throw RpcException.Unsupported(Loc.S("Bu özellik yalnızca masaüstü uygulamasında çalışır.", "This feature is only available in the desktop app."));
            using var key = Reg.Create(runKey);
            if (c.Bool("on")) key.SetValue("OptiCore", $"\"{exe}\" --minimized");
            else key.DeleteValue("OptiCore", false);
            return c.Bool("on");
        });

        // The desktop shell replaces these with real implementations.
        foreach (var method in new[] { "shell.relaunchElevated", "shell.pickFolder", "shell.pickFile", "shell.saveText", "shell.setTheme", "shell.overlay" })
            Rpc.On(method, _ => throw RpcException.Unsupported(Loc.S("Bu özellik yalnızca masaüstü uygulamasında çalışır.", "This feature is only available in the desktop app.")));
    }

    public void Dispose()
    {
        try { Processes.Flush(); } catch { /* shutting down */ }
        try { Gaming.Dispose(); } catch { /* shutting down */ }
        Metrics.Dispose();
        Log.Info("engine stopped");
    }
}
