using System.Diagnostics;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public sealed class MaintenanceTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>daily | weekly | monthly | idle | logon</summary>
    public string Schedule { get; set; } = "weekly";
    /// <summary>0 = Sunday (weekly) or day of month (monthly).</summary>
    public int Day { get; set; } = 0;
    public int Hour { get; set; } = 12;
    /// <summary>analyze | cleanup | startup-check | tweak-drift | benchmark | restore-point</summary>
    public List<string> Actions { get; set; } = ["analyze"];
    public List<string> CleanupCategories { get; set; } = [];
    /// <summary>Only run when the PC has been idle (no full-screen app, low CPU).</summary>
    public bool OnlyIdle { get; set; } = true;
    public bool OnlyOnAc { get; set; } = true;
    public DateTime? LastRun { get; set; }
}

public sealed class MaintenanceRun
{
    public string TaskId { get; set; } = "";
    public string Task { get; set; } = "";
    public DateTime Time { get; set; }
    public double Seconds { get; set; }
    public bool Ok { get; set; }
    public bool Simulated { get; set; }
    public List<LText> Lines { get; set; } = [];
}

/// <summary>
/// Automatic maintenance: user-defined tasks on a schedule, run in the background while OptiCore runs.
/// An optional Windows scheduled task starts OptiCore minimized at logon so the schedule keeps working.
/// </summary>
public sealed class AutomationService
{
    private const string TasksFile = "automation/tasks.json";
    private const string HistoryFile = "automation/history.jsonl";
    private const string WindowsTask = @"\OptiCore\Maintenance";

    private readonly DataStore _store;
    private readonly InsightsEngine _insights;
    private readonly CleanupService _cleanup;
    private readonly StartupService _startup;
    private readonly TweakService _tweaks;
    private readonly NotificationCenter _notices;
    private readonly MetricsSampler _metrics;
    private readonly ChangeJournal _journal;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _running;

    public AutomationService(DataStore store, InsightsEngine insights, CleanupService cleanup, StartupService startup, TweakService tweaks, NotificationCenter notices, MetricsSampler metrics, ChangeJournal journal)
    {
        _store = store; _insights = insights; _cleanup = cleanup; _startup = startup; _tweaks = tweaks; _notices = notices; _metrics = metrics; _journal = journal;
    }

    public static readonly (string Id, LText Title, LText Description, MaintenanceTask Template)[] Profiles =
    [
        ("light", Loc.T("Hafif", "Light"), Loc.T("Haftalık analiz ve geçici dosya temizliği.", "Weekly analysis and temporary file cleanup."),
            new MaintenanceTask { Name = "Hafif bakım / Light maintenance", Schedule = "weekly", Day = 0, Hour = 12, Actions = ["analyze", "cleanup"], CleanupCategories = ["temp.user", "thumbs", "wer"] }),
        ("standard", Loc.T("Standart", "Standard"), Loc.T("Haftalık analiz, temizlik, başlangıç denetimi ve geri alınmış ayarların tespiti.", "Weekly analysis, cleanup, startup check and detection of reset tweaks."),
            new MaintenanceTask { Name = "Standart bakım / Standard maintenance", Schedule = "weekly", Day = 0, Hour = 12, Actions = ["analyze", "cleanup", "startup-check", "tweak-drift"], CleanupCategories = ["temp.user", "thumbs", "wer", "browser.chrome", "browser.edge", "browser.firefox", "apps.electron"] }),
        ("deep", Loc.T("Kapsamlı", "Deep"), Loc.T("Aylık geri yükleme noktası, tam temizlik ve tüm denetimler.", "Monthly restore point, full cleanup and every check."),
            new MaintenanceTask { Name = "Kapsamlı bakım / Deep maintenance", Schedule = "monthly", Day = 1, Hour = 13, Actions = ["restore-point", "analyze", "cleanup", "startup-check", "tweak-drift"], CleanupCategories = ["temp.user", "temp.windows", "thumbs", "wer", "do.cache", "logs.windows", "browser.chrome", "browser.edge", "browser.firefox", "apps.electron", "apps.launchers"] }),
    ];

    public void Start() => _timer = new Timer(_ => { try { Tick(); } catch (Exception e) { Log.Warn("automation: tick", e); } }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));

    private List<MaintenanceTask> Tasks() => _store.Load(TasksFile, () => new List<MaintenanceTask>());
    private void SaveTasks(List<MaintenanceTask> tasks) => _store.Save(TasksFile, tasks);

    /// <summary>The most recent moment the schedule says the task should have run.</summary>
    internal static DateTime? DueSince(MaintenanceTask t, DateTime nowLocal)
    {
        DateTime today = nowLocal.Date.AddHours(t.Hour);
        DateTime candidate = t.Schedule switch
        {
            "daily" => today <= nowLocal ? today : today.AddDays(-1),
            "weekly" => Enumerable.Range(0, 8).Select(d => today.AddDays(-d)).First(d => (int)d.DayOfWeek == t.Day && d <= nowLocal),
            "monthly" => Enumerable.Range(0, 2).Select(m => new DateTime(nowLocal.Year, nowLocal.Month, 1).AddMonths(-m)).Select(m => m.AddDays(Math.Min(Math.Max(1, t.Day), DateTime.DaysInMonth(m.Year, m.Month)) - 1).AddHours(t.Hour)).First(d => d <= nowLocal),
            _ => DateTime.MinValue,
        };
        return candidate == DateTime.MinValue ? null : candidate;
    }

    private void Tick()
    {
        if (_running) return;
        var now = DateTime.Now;
        var tasks = Tasks();
        foreach (var task in tasks.Where(t => t.Enabled))
        {
            bool due = task.Schedule switch
            {
                "logon" => task.LastRun is null || task.LastRun < DateTime.UtcNow.AddMilliseconds(-Environment.TickCount64),
                "idle" => task.LastRun is null || (DateTime.UtcNow - task.LastRun.Value).TotalDays >= 1,
                _ => DueSince(task, now) is DateTime since && (task.LastRun is null || task.LastRun.Value.ToLocalTime() < since),
            };
            if (!due) continue;
            var s = _metrics.Recent(30);
            if (task.OnlyIdle && (ShellOps.IsFullscreenBusy() || (s.Count > 5 && s.Average(x => x.Cpu) > 25))) continue;
            if (task.OnlyOnAc && _metrics.Latest is { OnBattery: true }) continue;
            Run(task);
            break; // one task per tick
        }
    }

    public MaintenanceRun Run(MaintenanceTask task)
    {
        lock (_gate)
        {
            if (_running) throw RpcException.Failed(Loc.S("Başka bir bakım görevi çalışıyor.", "Another maintenance task is running."));
            _running = true;
        }
        var run = new MaintenanceRun { TaskId = task.Id, Task = task.Name, Time = DateTime.UtcNow, Simulated = _journal.DryRun, Ok = true };
        var sw = Stopwatch.StartNew();
        try
        {
            foreach (var action in task.Actions)
            {
                try
                {
                    switch (action)
                    {
                        case "analyze":
                            var report = _insights.Analyze(true);
                            run.Lines.Add(Loc.T("Analiz: sağlık puanı {0}, {1} kritik, {2} uyarı.", "Analysis: health score {0}, {1} critical, {2} warnings.").F(report.Score, report.Critical, report.Warnings));
                            break;
                        case "cleanup":
                            var scan = _cleanup.Scan(null);
                            var ids = scan.Categories.Where(c => task.CleanupCategories.Contains(c.Id) && c.Available && c.Bytes > 0).Select(c => c.Id).ToArray();
                            if (ids.Length == 0) { run.Lines.Add(Loc.T("Temizlik: temizlenecek bir şey yok.", "Cleanup: nothing to clean.")); break; }
                            var job = new JobContext(new Job { Id = "auto", Kind = "auto" });
                            dynamic r = _cleanup.Clean(ids, job);
                            run.Lines.Add(Loc.T("Temizlik: {0} yer açıldı ({1} kategori).", "Cleanup: {0} freed ({1} categories).").F(new ByteSize((long)r.freedBytes), ids.Length));
                            break;
                        case "startup-check":
                            int enabled = _startup.List(true).Count(e => e.Enabled && e.Kind != "service");
                            int flagged = _startup.List().Count(e => e.Enabled && e.Flags.Count > 0);
                            run.Lines.Add(Loc.T("Başlangıç: {0} etkin öğe, {1} şüpheli.", "Startup: {0} enabled items, {1} suspicious.").F(enabled, flagged));
                            break;
                        case "tweak-drift":
                            var drift = _tweaks.Drifted();
                            if (drift.Count > 0) _tweaks.ApplyMany([.. drift.Select(d => d.Id)], true, Loc.T("Otomatik bakım: geri alınmış ayarlar yeniden uygulandı", "Maintenance: reset tweaks re-applied"));
                            run.Lines.Add(drift.Count > 0 ? Loc.T("Ayarlar: {0} geri alınmış ayar yeniden uygulandı.", "Tweaks: {0} reset tweaks re-applied.").F(drift.Count) : Loc.T("Ayarlar: değişiklik yok.", "Tweaks: no drift."));
                            break;
                        case "restore-point":
                            if (!Elevation.IsElevated) { run.Lines.Add(Loc.T("Geri yükleme noktası: yönetici yetkisi olmadığı için atlandı.", "Restore point: skipped, no administrator rights.")); break; }
                            if (!_journal.DryRun)
                            {
                                var rp = Proc.PowerShell("Checkpoint-Computer -Description 'OptiCore bakım' -RestorePointType MODIFY_SETTINGS", 300000);
                                run.Lines.Add(rp.Ok ? Loc.T("Geri yükleme noktası oluşturuldu.", "Restore point created.") : Loc.T("Geri yükleme noktası oluşturulamadı.", "The restore point could not be created."));
                            }
                            break;
                    }
                }
                catch (Exception e)
                {
                    run.Ok = false;
                    run.Lines.Add(Loc.T("{0}: hata — {1}", "{0}: error — {1}").F(action, e.Message));
                    Log.Warn("automation: " + action, e);
                }
            }
        }
        finally
        {
            run.Seconds = sw.Elapsed.TotalSeconds;
            _store.Append(HistoryFile, run);
            var all = Tasks();
            var stored = all.FirstOrDefault(t => t.Id == task.Id);
            if (stored is not null) { stored.LastRun = DateTime.UtcNow; SaveTasks(all); }
            lock (_gate) _running = false;
        }
        _notices.Add(run.Ok ? NoticeLevel.Success : NoticeLevel.Warn, Loc.T("Bakım tamamlandı: {0}", "Maintenance finished: {0}").F(task.Name), string.Join(" ", run.Lines.Select(l => l.ToString())), "automation");
        return run;
    }

    // ------------------------------------------------------------------ Windows scheduled task (runs OptiCore at logon)
    private static bool WindowsTaskExists() => TaskSched.GetEnabled(WindowsTask) is not null;

    private static void SetWindowsTask(bool on, string exe)
    {
        if (!on) { Proc.Run(Proc.Sys("schtasks.exe"), $"/Delete /TN \"{WindowsTask}\" /F"); return; }
        var r = Proc.Run(Proc.Sys("schtasks.exe"), $"/Create /TN \"{WindowsTask}\" /TR \"\\\"{exe}\\\" --minimized\" /SC ONLOGON /RL LIMITED /F /DELAY 0002:00");
        if (!r.Ok) throw r.Text.Contains("denied", StringComparison.OrdinalIgnoreCase) || r.Text.Contains("reddedildi", StringComparison.OrdinalIgnoreCase) ? new UnauthorizedAccessException(r.Text) : new InvalidOperationException(r.Text);
    }

    public void Register(RpcRouter rpc, Func<string?> appExe)
    {
        rpc.On("auto.tasks", _ => new
        {
            tasks = Tasks().Select(t => new { task = t, next = Next(t) }).ToList(),
            profiles = Profiles.Select(p => new { p.Id, p.Title, p.Description }).ToList(),
            windowsTask = WindowsTaskExists(), canSchedule = appExe() is not null, running = _running,
        });
        rpc.On("auto.save", c =>
        {
            var task = c.Get<MaintenanceTask>("task");
            if (task.Schedule is not ("daily" or "weekly" or "monthly" or "idle" or "logon")) throw RpcException.Invalid("schedule");
            var all = Tasks();
            var old = all.FirstOrDefault(t => t.Id == task.Id);
            task.LastRun ??= old?.LastRun ?? DateTime.UtcNow; // a new task waits for its first scheduled time
            all.RemoveAll(t => t.Id == task.Id);
            all.Add(task);
            SaveTasks(all);
            return all;
        });
        rpc.On("auto.delete", c => { var all = Tasks(); all.RemoveAll(t => t.Id == c.Str("id")); SaveTasks(all); return all; });
        rpc.On("auto.addProfile", c =>
        {
            var p = Profiles.FirstOrDefault(x => x.Id == c.Str("id"));
            if (p.Id is null) throw RpcException.NotFound("profile");
            var t = System.Text.Json.JsonSerializer.Deserialize<MaintenanceTask>(System.Text.Json.JsonSerializer.Serialize(p.Template, Json.Store), Json.Store)!;
            t.Id = Guid.NewGuid().ToString("N")[..8];
            t.LastRun = DateTime.UtcNow;
            var all = Tasks();
            all.Add(t);
            SaveTasks(all);
            return all;
        });
        rpc.On("auto.runNow", c => Run(Tasks().FirstOrDefault(t => t.Id == c.Str("id")) ?? throw RpcException.NotFound("task")));
        rpc.On("auto.history", _ => _store.ReadLines<MaintenanceRun>(HistoryFile).TakeLast(100).Reverse().ToList());
        rpc.On("auto.windowsTask", c =>
        {
            string exe = appExe() ?? throw RpcException.Unsupported(Loc.S("Bu özellik yalnızca masaüstü uygulamasında çalışır.", "This feature is only available in the desktop app."));
            SetWindowsTask(c.Bool("on"), exe);
            return WindowsTaskExists();
        });
    }

    private static DateTime? Next(MaintenanceTask t)
    {
        if (!t.Enabled || t.Schedule is "idle" or "logon") return null;
        var since = DueSince(t, DateTime.Now);
        if (since is null) return null;
        DateTime next = t.Schedule switch { "daily" => since.Value.AddDays(1), "weekly" => since.Value.AddDays(7), _ => since.Value.AddMonths(1) };
        if (t.LastRun is null || t.LastRun.Value.ToLocalTime() < since.Value) next = since.Value; // overdue: runs at the next idle moment
        return next.ToUniversalTime();
    }
}
