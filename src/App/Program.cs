using System.Diagnostics;
using OptiCore.Engine;
using OptiCore.Engine.Core;

namespace OptiCore.App;

internal static class Program
{
    private const string MutexName = @"Local\OptiCore.SingleInstance";
    private const string ActivateEvent = @"Local\OptiCore.Activate";

    [STAThread]
    private static int Main(string[] args)
    {
        bool dryRun = args.Contains("--dry-run");
        bool minimized = args.Contains("--minimized");
        string? dataDir = Arg(args, "--data-dir");

        // One OptiCore per user session and data folder: a second launch just brings the first window forward.
        // A separate --data-dir (testing, a portable copy) is a separate instance.
        string suffix = dataDir is null ? "" : "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).ToLowerInvariant())))[..12];
        using var mutex = new Mutex(true, MutexName + suffix, out bool first);
        if (!first)
        {
            // An elevated relaunch waits briefly for the previous instance to exit.
            if (!args.Contains("--relaunch") || !mutex.WaitOne(TimeSpan.FromSeconds(10)))
            {
                try { using var ev = EventWaitHandle.OpenExisting(ActivateEvent + suffix); ev.Set(); } catch { /* the other instance is closing */ }
                return 0;
            }
        }
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEvent + suffix);

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("ui thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled exception", e.ExceptionObject as Exception);

        using var engine = new OptiCoreEngine(new EngineOptions { DryRun = dryRun, DataDir = dataDir, Host = "desktop" })
        {
            AppExecutable = Environment.ProcessPath,
        };
        engine.Start();

        using var form = new MainForm(engine, args, minimized || engine.Settings.Current.StartMinimized);
        var waiter = new Thread(() =>
        {
            while (activate.WaitOne())
            {
                try { form.BeginInvoke(form.BringToFrontFromTray); } catch { return; }
            }
        }) { IsBackground = true, Name = "OptiCore.Activate" };
        waiter.Start();

        Application.Run(form);
        try { mutex.ReleaseMutex(); } catch { /* not owned */ }
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Starts an elevated copy with the same arguments; the caller exits when it returns true.</summary>
    public static bool RelaunchElevated(string[] args)
    {
        var keep = args.Where(a => a is not "--relaunch" and not "--minimized").Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(" ", keep.Append("--relaunch"))) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return false; // the user declined the UAC prompt
        }
    }
}
