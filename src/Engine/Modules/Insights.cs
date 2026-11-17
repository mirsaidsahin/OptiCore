using OptiCore.Engine.Core;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public enum Severity { Good, Info, Warning, Critical }

public sealed record Evidence(LText Label, LText Value);

/// <summary>Something the user can do about a finding: open a page, or run an engine method.</summary>
public sealed class FindingAction
{
    public LText Label { get; set; }
    /// <summary>UI route to open, e.g. "cleanup".</summary>
    public string? Route { get; set; }
    /// <summary>Engine method to call instead of navigating.</summary>
    public string? Method { get; set; }
    public object? Params { get; set; }
    /// <summary>True when the fix is low-risk, reversible and may be part of one-click optimization.</summary>
    public bool Safe { get; set; }
}

/// <summary>
/// A measured problem (or a confirmed strength). Every finding carries the numbers it was derived
/// from, so nothing in OptiCore is an unexplained verdict.
/// </summary>
public sealed class Finding
{
    public string Id { get; set; } = "";
    /// <summary>Score group: performance | storage | security | stability | maintenance.</summary>
    public string Group { get; set; } = "performance";
    public Severity Severity { get; set; } = Severity.Info;
    public LText Title { get; set; }
    public LText Detail { get; set; }
    public List<Evidence> Evidence { get; set; } = [];
    public List<FindingAction> Actions { get; set; } = [];
    /// <summary>What fixing it is expected to change, stated as an estimate.</summary>
    public LText? Impact { get; set; }
    /// <summary>Points this finding takes from its group's score.</summary>
    public int Penalty { get; set; }
    public bool Ignored { get; set; }

    public Finding Ev(LText label, LText value) { Evidence.Add(new Evidence(label, value)); return this; }
    public Finding Go(LText label, string route) { Actions.Add(new FindingAction { Label = label, Route = route }); return this; }
    public Finding Fix(LText label, string method, object? parameters = null, bool safe = false) { Actions.Add(new FindingAction { Label = label, Method = method, Params = parameters, Safe = safe }); return this; }
}

public sealed class ScorePart
{
    public string Id { get; set; } = "";
    public LText Label { get; set; }
    public int Score { get; set; }
    public int Weight { get; set; }
    public int Findings { get; set; }
}

public sealed class HealthReport
{
    public DateTime Time { get; set; }
    public int Score { get; set; }
    /// <summary>excellent | good | fair | poor</summary>
    public string Grade { get; set; } = "good";
    public List<ScorePart> Parts { get; set; } = [];
    public List<Finding> Findings { get; set; } = [];
    public int Critical { get; set; }
    public int Warnings { get; set; }
    /// <summary>True when only the fast checks ran (the full analysis is still pending).</summary>
    public bool Quick { get; set; }
}

public sealed class HealthPoint { public long T { get; set; } public int Score { get; set; } public Dictionary<string, int> Parts { get; set; } = []; }

public sealed class Baseline
{
    public string Id { get; set; } = "";
    public DateTime Time { get; set; }
    public string Label { get; set; } = "";
    public double CpuIdle { get; set; }
    public long MemoryUsed { get; set; }
    public double MemoryPercent { get; set; }
    public int Processes { get; set; }
    public int Threads { get; set; }
    public long SystemFree { get; set; }
    public int HealthScore { get; set; }
    public long UptimeSeconds { get; set; }
    public Dictionary<string, double> Extra { get; set; } = [];
}

/// <summary>
/// Performance Intelligence: turns raw measurements into findings, a transparent health score,
/// a live "why is it slow right now" diagnosis and before/after comparisons.
/// Other modules contribute their own findings through <see cref="AddSource"/>.
/// </summary>
public sealed class InsightsEngine
{
    private static readonly (string Id, LText Label, int Weight)[] Groups =
    [
        ("performance", Loc.T("Performans", "Performance"), 25),
        ("security", Loc.T("Güvenlik", "Security"), 25),
        ("stability", Loc.T("Kararlılık", "Stability"), 20),
        ("storage", Loc.T("Depolama", "Storage"), 15),
        ("maintenance", Loc.T("Bakım", "Maintenance"), 15),
    ];

    private readonly MetricsSampler _metrics;
    private readonly ProcessSampler _processes;
    private readonly SystemInfoService _system;
    private readonly SettingsService _settings;
    private readonly DataStore _store;
    private readonly JobManager _jobs;
    private readonly List<(string Name, bool Slow, Func<IEnumerable<Finding>> Read)> _sources = [];
    private readonly Dictionary<string, Func<double>> _baselineProbes = [];
    private readonly object _gate = new();
    private HealthReport? _last;
    private List<Finding> _slowCache = [];
    private DateTime _slowAt;

    public InsightsEngine(MetricsSampler metrics, ProcessSampler processes, SystemInfoService system, SettingsService settings, DataStore store, JobManager jobs)
    {
        _metrics = metrics; _processes = processes; _system = system; _settings = settings; _store = store; _jobs = jobs;
    }

    /// <summary>Registers a module's findings. Slow sources (event logs, WMI inventories) only run in the full analysis.</summary>
    public void AddSource(string name, Func<IEnumerable<Finding>> read, bool slow = false) => _sources.Add((name, slow, read));

    /// <summary>Adds a number to before/after snapshots, e.g. enabled startup items.</summary>
    public void AddBaselineProbe(string key, Func<double> read) => _baselineProbes[key] = read;

    public HealthReport? Last => _last;

    // ------------------------------------------------------------------ analysis
    public HealthReport Analyze(bool full, JobContext? job = null)
    {
        var findings = new List<Finding>();
        var sources = _sources.Where(s => !s.Slow || full).ToList();
        int done = 0;

        void Run(string name, Func<IEnumerable<Finding>> read)
        {
            try { findings.AddRange(read()); }
            catch (Exception e) { Log.Warn("insights: source " + name, e); }
            job?.Report(++done / (double)(sources.Count + 1));
        }

        job?.Say(Loc.T("Canlı ölçümler değerlendiriliyor…", "Evaluating live measurements…"));
        Run("core", CoreFindings);
        foreach (var (name, _, read) in sources)
        {
            job?.ThrowIfCancelled();
            job?.Say(Loc.T("İnceleniyor: {0}", "Checking: {0}").F(name));
            Run(name, read);
        }

        if (full)
        {
            lock (_gate) { _slowCache = [.. findings.Where(f => _sources.Any(s => s.Slow && f.Id.StartsWith(s.Name + ".", StringComparison.Ordinal)))]; _slowAt = DateTime.UtcNow; }
        }
        else
        {
            // Keep the results of the last full analysis for the slow checks instead of dropping them.
            lock (_gate) findings.AddRange(_slowCache.Where(c => findings.All(f => f.Id != c.Id)));
        }

        var ignored = _settings.Current.IgnoredFindings;
        foreach (var f in findings) f.Ignored = ignored.Contains(f.Id);

        var report = Score(findings);
        report.Quick = !full && _slowAt == default;
        lock (_gate) _last = report;
        if (full)
            _store.Append(Path.Combine("history", "health.jsonl"), new HealthPoint
            {
                T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Score = report.Score, Parts = report.Parts.ToDictionary(p => p.Id, p => p.Score),
            });
        return report;
    }

    internal static HealthReport Score(List<Finding> findings)
    {
        var report = new HealthReport { Time = DateTime.UtcNow };
        double total = 0, weights = 0;
        foreach (var (id, label, weight) in Groups)
        {
            var mine = findings.Where(f => f.Group == id && !f.Ignored && f.Severity >= Severity.Warning).ToList();
            int score = Math.Clamp(100 - mine.Sum(f => f.Penalty), 0, 100);
            report.Parts.Add(new ScorePart { Id = id, Label = label, Score = score, Weight = weight, Findings = mine.Count });
            total += score * weight;
            weights += weight;
        }
        report.Score = (int)Math.Round(total / weights);
        // One critical problem caps the headline score: a PC with a failing disk is not "88, good".
        if (findings.Any(f => !f.Ignored && f.Severity == Severity.Critical)) report.Score = Math.Min(report.Score, 74);
        report.Grade = report.Score >= 90 ? "excellent" : report.Score >= 75 ? "good" : report.Score >= 55 ? "fair" : "poor";
        report.Findings = [.. findings.OrderByDescending(f => f.Ignored ? -1 : (int)f.Severity).ThenByDescending(f => f.Penalty)];
        report.Critical = findings.Count(f => !f.Ignored && f.Severity == Severity.Critical);
        report.Warnings = findings.Count(f => !f.Ignored && f.Severity == Severity.Warning);
        return report;
    }

    private IEnumerable<Finding> CoreFindings()
    {
        var hw = _system.Hardware();
        var recent = _metrics.Recent(60);
        var now = _metrics.Latest;
        var procs = _processes.Snapshot(4000).Where(p => p.Pid > 4).ToList();
        string Top(Func<ProcInfo, double> by, Func<ProcInfo, string> fmt) => string.Join(", ",
            procs.GroupBy(p => p.Description ?? p.Name).Select(g => (g.Key, V: g.Sum(by), First: g.First())).OrderByDescending(x => x.V).Take(3).Select(x => $"{x.Key} ({fmt(new ProcInfo { Cpu = x.V, Memory = (long)x.V })})"));

        // ---- memory
        if (recent.Count >= 5)
        {
            double mem = recent.Average(s => s.MemPercent);
            double faults = recent.Average(s => s.HardFaults);
            if (mem >= 85)
            {
                var f = new Finding
                {
                    Id = "core.memory.pressure", Group = "performance", Severity = mem >= 93 ? Severity.Critical : Severity.Warning, Penalty = mem >= 93 ? 18 : 9,
                    Title = Loc.T("Bellek neredeyse dolu", "Memory is almost full"),
                    Detail = Loc.T("Fiziksel bellek dolduğunda Windows verileri diske sayfalar; uygulamalar arasında geçiş yavaşlar ve takılmalar başlar.",
                                   "When physical memory fills up Windows pages data to disk; switching between apps slows down and stutters appear."),
                    Impact = Loc.T("En çok bellek tutan uygulamaları kapatmak anında rahatlatır.", "Closing the heaviest apps brings immediate relief."),
                };
                f.Ev(Loc.T("Kullanım", "In use"), Loc.T("%{0:0}", "{0:0}%").F(mem));
                if (now is not null) f.Ev(Loc.T("Boş", "Available"), Loc.T("{0}", "{0}").F(new ByteSize(now.MemAvailable)));
                f.Ev(Loc.T("Sayfa hatası", "Hard faults"), Loc.T("{0:0}/sn", "{0:0}/s").F(faults));
                f.Ev(Loc.T("En çok kullananlar", "Top consumers"), Top(p => p.Memory, p => new ByteSize(p.Memory).ToString()));
                yield return f.Go(Loc.T("İşlemleri göster", "Show processes"), "processes/memory");
            }
            if (now is not null && now.CommitLimit > 0 && now.Commit > now.CommitLimit * 0.92)
                yield return new Finding
                {
                    Id = "core.memory.commit", Group = "performance", Severity = Severity.Warning, Penalty = 8,
                    Title = Loc.T("Sanal bellek sınırına yaklaşıldı", "Close to the virtual memory limit"),
                    Detail = Loc.T("Ayrılan bellek, RAM ve disk belleği dosyasının toplamına dayandı. Sınır aşılırsa uygulamalar \"bellek yetersiz\" hatasıyla kapanır.",
                                   "Committed memory is near the sum of RAM and the page file. Past the limit, apps close with out-of-memory errors."),
                }.Ev(Loc.T("Ayrılan", "Committed"), Loc.T("{0} / {1}", "{0} / {1}").F(new ByteSize(now.Commit), new ByteSize(now.CommitLimit)))
                 .Go(Loc.T("İşlemleri göster", "Show processes"), "processes/memory");

            // ---- processor
            double cpu = recent.Average(s => s.Cpu);
            if (cpu >= 80 && recent.Count >= 30)
                yield return new Finding
                {
                    Id = "core.cpu.busy", Group = "performance", Severity = Severity.Warning, Penalty = 8,
                    Title = Loc.T("İşlemci sürekli yüksek yükte", "The processor is under sustained load"),
                    Detail = Loc.T("Son bir dakikanın ortalaması çok yüksek. Bilerek çalıştırdığınız bir iş değilse aşağıdaki uygulamalara bakın.",
                                   "The last minute averaged very high. Unless it is work you started on purpose, look at the apps below."),
                }.Ev(Loc.T("1 dk ortalama", "1 min average"), Loc.T("%{0:0}", "{0:0}%").F(cpu))
                 .Ev(Loc.T("En çok kullananlar", "Top consumers"), Top(p => p.Cpu, p => p.Cpu.ToString("0") + "%"))
                 .Go(Loc.T("İşlemleri göster", "Show processes"), "processes/cpu");

            // ---- thermal throttling
            int throttled = recent.Count(s => s.Throttled);
            if (throttled >= 5)
            {
                var f = new Finding
                {
                    Id = "core.thermal.throttle", Group = "performance", Severity = Severity.Warning, Penalty = 12,
                    Title = Loc.T("İşlemci ısı nedeniyle yavaşlatılıyor", "The processor is being throttled by heat"),
                    Detail = Loc.T("Donanım yazılımı sıcaklığı düşürmek için işlemci hızını kısıyor. Hava girişlerini kontrol edin; dizüstünü sert ve düz bir zemine alın; toz ve eskimiş termal macun en sık nedenlerdir.",
                                   "The firmware is capping CPU speed to shed heat. Check the air intakes; put the laptop on a hard, flat surface; dust and dried thermal paste are the usual causes."),
                    Impact = Loc.T("Soğutma düzeldiğinde işlemci tam hızına döner.", "Once cooling improves the processor returns to full speed."),
                };
                f.Ev(Loc.T("Kısılan süre", "Throttled"), Loc.T("son {0} sn içinde {1} sn", "{1}s of the last {0}s").F(recent.Count, throttled));
                var limit = recent.SelectMany(s => s.Zones).Select(z => z.PassiveLimit).DefaultIfEmpty(100).Min();
                if (limit < 100) f.Ev(Loc.T("Pasif sınır", "Passive limit"), Loc.T("%{0:0}", "{0:0}%").F(limit));
                if (now?.Temp is double tc) f.Ev(Loc.T("Sıcaklık", "Temperature"), Loc.T("{0:0} °C", "{0:0} °C").F(tc));
                yield return f.Go(Loc.T("Termal ayrıntılar", "Thermal details"), "system/thermal");
            }

            // ---- disk latency
            var busy = recent.Where(s => s.DiskActive > 85).ToList();
            if (busy.Count >= 20)
            {
                double lat = busy.SelectMany(s => s.Disks).Where(d => d.Active > 85).Select(d => d.LatencyMs).DefaultIfEmpty(0).Average();
                if (lat > 40)
                    yield return new Finding
                    {
                        Id = "core.disk.latency", Group = "performance", Severity = Severity.Warning, Penalty = 8,
                        Title = Loc.T("Disk isteklere yetişemiyor", "The disk cannot keep up"),
                        Detail = Loc.T("Disk neredeyse sürekli meşgul ve yanıt süresi yüksek. Bu, sistemin tamamını bekleten tipik bir darboğazdır.",
                                       "The disk is busy almost all the time and its response time is high. This is a classic bottleneck that makes everything wait."),
                    }.Ev(Loc.T("Ortalama yanıt", "Average response"), Loc.T("{0:0} ms", "{0:0} ms").F(lat))
                     .Go(Loc.T("İşlemleri göster", "Show processes"), "processes/disk");
            }
        }

        // ---- storage
        foreach (var v in _system.Volumes().Where(v => v.Kind == "fixed" && v.TotalBytes > 8L << 30))
        {
            double free = 100.0 * v.FreeBytes / v.TotalBytes;
            double warnAt = v.IsSystem ? 15 : 6, badAt = v.IsSystem ? 8 : 2;
            if (free >= warnAt) continue;
            var f = new Finding
            {
                Id = "core.disk.free." + v.Letter, Group = "storage", Severity = free < badAt ? Severity.Critical : Severity.Warning, Penalty = free < badAt ? 30 : 15,
                Title = Loc.T("{0}: sürücüsünde yer azaldı", "Drive {0}: is running low on space").F(v.Letter),
                Detail = v.IsSystem
                    ? Loc.T("Windows güncellemeler, sanal bellek ve geçici dosyalar için boş alana ihtiyaç duyar. SSD'ler de doldukça yavaşlar.",
                            "Windows needs free space for updates, virtual memory and temporary files. SSDs also slow down as they fill up.")
                    : Loc.T("Bu sürücü neredeyse dolu.", "This drive is almost full."),
            };
            f.Ev(Loc.T("Boş alan", "Free"), Loc.T("{0} (%{1:0})", "{0} ({1:0}%)").F(new ByteSize(v.FreeBytes), free));
            f.Ev(Loc.T("Kapasite", "Capacity"), new ByteSize(v.TotalBytes).ToString());
            yield return f.Go(Loc.T("Temizlik taraması", "Run a cleanup scan"), "cleanup").Go(Loc.T("Yer kaplayanları bul", "Find what takes space"), "storage/map");
        }

        foreach (var d in hw.Disks)
        {
            if (d.Health is "warning" or "unhealthy")
                yield return new Finding
                {
                    Id = "core.disk.health." + d.Index, Group = "stability", Severity = Severity.Critical, Penalty = 45,
                    Title = Loc.T("Disk arıza belirtisi gösteriyor: {0}", "A disk is reporting trouble: {0}").F(d.Model),
                    Detail = Loc.T("Windows bu sürücünün sağlık durumunu sorunlu olarak bildiriyor. Önemli verilerinizi hemen yedekleyin.",
                                   "Windows reports this drive's health as degraded. Back up your important data now."),
                }.Ev(Loc.T("Durum", "Status"), d.Health).Go(Loc.T("Depolama ayrıntıları", "Storage details"), "storage");
            if (d.WearPercent is >= 85)
                yield return new Finding
                {
                    Id = "core.disk.wear." + d.Index, Group = "stability", Severity = Severity.Warning, Penalty = 15,
                    Title = Loc.T("SSD ömrünün sonuna yaklaşıyor: {0}", "An SSD is nearing the end of its life: {0}").F(d.Model),
                    Detail = Loc.T("Sürücü, tasarlanan yazma ömrünün büyük kısmını kullandı. Yedeklerinizi güncel tutun ve değişim planlayın.",
                                   "The drive has used most of its rated write endurance. Keep backups current and plan a replacement."),
                }.Ev(Loc.T("Kullanılan ömür", "Life used"), Loc.T("%{0:0}", "{0:0}%").F(d.WearPercent!.Value)).Go(Loc.T("Depolama ayrıntıları", "Storage details"), "storage");
            if (d.IsSystem && d.Media == "HDD")
                yield return new Finding
                {
                    Id = "core.disk.hdd", Group = "performance", Severity = Severity.Warning, Penalty = 14,
                    Title = Loc.T("Windows bir sabit diskte (HDD) çalışıyor", "Windows runs from a hard disk (HDD)"),
                    Detail = Loc.T("Bu, bu bilgisayarda yapılabilecek en etkili yükseltmedir: SSD'ye geçmek açılışı, uygulama başlatmayı ve güncellemeleri birkaç kat hızlandırır. Hiçbir yazılım ayarı aynı farkı yaratmaz.",
                                   "This is the single most effective upgrade for this PC: moving to an SSD makes boot, app launch and updates several times faster. No software tweak comes close."),
                    Impact = Loc.T("Açılış ve uygulama başlatma: tipik olarak 3-5 kat daha hızlı.", "Boot and app launch: typically 3-5x faster."),
                }.Ev(Loc.T("Sistem diski", "System disk"), d.Model);
        }

        // ---- memory size
        long ram = hw.Memory.InstalledBytes;
        if (ram is > 0 and < 7L << 30)
        {
            var f = new Finding
            {
                Id = "core.memory.small", Group = "performance", Severity = ram < 3500L << 20 ? Severity.Critical : Severity.Warning, Penalty = ram < 3500L << 20 ? 20 : 10,
                Title = Loc.T("Takılı bellek az", "Installed memory is low"),
                Detail = Loc.T("Windows 11 ve günümüz tarayıcıları için 8 GB pratik alt sınırdır. Daha azında sistem sık sık diske sayfalar.",
                               "For Windows 11 and today's browsers 8 GB is the practical minimum. Below that the system pages to disk constantly."),
            };
            f.Ev(Loc.T("Takılı", "Installed"), new ByteSize(ram).ToString());
            if (hw.Memory.SlotsTotal > 0) f.Ev(Loc.T("Yuvalar", "Slots"), Loc.T("{0} / {1} dolu", "{0} of {1} used").F(hw.Memory.SlotsUsed, hw.Memory.SlotsTotal));
            if (hw.Memory.MaxCapacityBytes > ram) f.Ev(Loc.T("Desteklenen en fazla", "Maximum supported"), new ByteSize(hw.Memory.MaxCapacityBytes).ToString());
            yield return f.Go(Loc.T("Bellek ayrıntıları", "Memory details"), "system/memory");
        }

        // ---- uptime
        double days = hw.Os.UptimeSeconds / 86400.0;
        if (days >= 10)
            yield return new Finding
            {
                Id = "core.uptime", Group = "maintenance", Severity = days >= 21 ? Severity.Warning : Severity.Info, Penalty = days >= 21 ? 8 : 0,
                Title = Loc.T("Bilgisayar {0:0} gündür yeniden başlatılmadı", "The PC has not restarted for {0:0} days").F(days),
                Detail = hw.Os.FastStartup
                    ? Loc.T("Hızlı Başlangıç açıkken \"Kapat\" çekirdeği gerçekten sıfırlamaz; sızan bellek ve bekleyen güncellemeler birikir. \"Yeniden başlat\" seçeneği tam bir sıfırlama yapar.",
                            "With Fast Startup on, \"Shut down\" does not really reset the kernel; leaked memory and pending updates pile up. \"Restart\" does a full reset.")
                    : Loc.T("Uzun çalışma sürelerinde sızan bellek ve bekleyen güncellemeler birikir. Yeniden başlatmak bunları temizler.",
                            "Over long uptimes leaked memory and pending updates pile up. A restart clears them."),
            }.Ev(Loc.T("Çalışma süresi", "Uptime"), Loc.T("{0:0.0} gün", "{0:0.0} days").F(days));

        if (Reg.Exists(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
            || Reg.Exists(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            yield return new Finding
            {
                Id = "core.reboot.pending", Group = "maintenance", Severity = Severity.Warning, Penalty = 8,
                Title = Loc.T("Güncellemeler yeniden başlatma bekliyor", "Updates are waiting for a restart"),
                Detail = Loc.T("Windows indirdiği güncellemeleri tamamlamak için yeniden başlatılmayı bekliyor. O zamana kadar güvenlik düzeltmeleri etkin değildir.",
                               "Windows is waiting for a restart to finish installing updates. Until then the security fixes are not active."),
            };

        // ---- power plan
        var plans = PowerPlans.List();
        var active = plans.FirstOrDefault(p => p.Active);
        if (active is not null && active.Kind == "saver" && now is { OnBattery: false })
            yield return new Finding
            {
                Id = "core.power.saver", Group = "performance", Severity = Severity.Warning, Penalty = 8,
                Title = Loc.T("Prize takılıyken \"Güç tasarrufu\" planı etkin", "\"Power saver\" is active while plugged in"),
                Detail = Loc.T("Bu plan işlemci hızını bilinçli olarak sınırlar. Prizdeyken \"Dengeli\" planı aynı sessizlikte belirgin biçimde daha hızlıdır.",
                               "This plan deliberately limits processor speed. On AC power the \"Balanced\" plan is noticeably faster and just as quiet."),
                Impact = Loc.T("Yük altında işlemci hızı sınırı kalkar.", "Removes the cap on processor speed under load."),
            }.Ev(Loc.T("Etkin plan", "Active plan"), active.Name).Go(Loc.T("Güç planları", "Power plans"), "tweaks/power");

        // ---- battery wear
        if (hw.Computer.Type == "laptop")
        {
            var battery = SystemInfoService.Battery();
            if (battery.Present && battery.HealthPercent is double health && health < 70)
                yield return new Finding
                {
                    Id = "core.battery.wear", Group = "maintenance", Severity = health < 50 ? Severity.Warning : Severity.Info, Penalty = health < 50 ? 8 : 0,
                    Title = Loc.T("Pil kapasitesinin %{0:0} kadarını koruyor", "The battery holds {0:0}% of its original capacity").F(health),
                    Detail = Loc.T("Piller yaşlandıkça kapasite kaybeder. Bu düzeyde pilden çalışma süresi belirgin biçimde kısalır.",
                                   "Batteries lose capacity as they age. At this level the runtime on battery is noticeably shorter."),
                }.Ev(Loc.T("Tasarım / şimdi", "Design / now"), Loc.T("{0:0.0} Wh / {1:0.0} Wh", "{0:0.0} Wh / {1:0.0} Wh").F(battery.DesignMwh / 1000.0, battery.FullMwh / 1000.0))
                 .Go(Loc.T("Pil ayrıntıları", "Battery details"), "system/battery");
        }

        // ---- graphics driver age
        foreach (var gpu in hw.Gpus)
        {
            if (gpu.DriverDate is not DateTime dd) continue;
            double months = (DateTime.UtcNow - dd).TotalDays / 30.4;
            if (months < 18) continue;
            yield return new Finding
            {
                Id = "core.gpu.driver", Group = "stability", Severity = months > 36 ? Severity.Warning : Severity.Info, Penalty = months > 36 ? 6 : 0,
                Title = Loc.T("Ekran kartı sürücüsü {0:0} aylık", "The graphics driver is {0:0} months old").F(months),
                Detail = Loc.T("Yeni sürücüler oyun uyumluluğu, video hızlandırma ve kararlılık düzeltmeleri getirir. Sürücüyü yalnızca üreticinin resmî sayfasından indirin.",
                               "Newer drivers bring game compatibility, video acceleration and stability fixes. Download drivers only from the manufacturer's official page."),
            }.Ev(Loc.T("Ekran kartı", "Graphics"), gpu.Name).Ev(Loc.T("Sürücü", "Driver"), Loc.T("{0} ({1:yyyy-MM-dd})", "{0} ({1:yyyy-MM-dd})").F(gpu.DriverVersion, dd))
             .Go(Loc.T("Sürücüler", "Drivers"), "drivers");
        }
    }

    // ------------------------------------------------------------------ live diagnosis
    private object LiveDiagnosis(JobContext job)
    {
        const int seconds = 8;
        var cpuByApp = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var ioByApp = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var gpuByApp = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var memByApp = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var samples = new List<LiveSample>();
        _processes.Snapshot(0);

        for (int i = 0; i < seconds; i++)
        {
            job.ThrowIfCancelled();
            job.Report(i / (double)seconds, Loc.T("Sistem dinleniyor… {0}/{1} sn", "Listening to the system… {0}/{1}s").F(i + 1, seconds));
            _metrics.Touch();
            Thread.Sleep(1000);
            if (_metrics.Latest is { } s) samples.Add(s);
            memByApp.Clear();
            foreach (var g in _processes.Snapshot(500).Where(p => p.Pid > 4 && p.Pid != Environment.ProcessId).GroupBy(p => p.Description ?? p.Name))
            {
                cpuByApp[g.Key] = cpuByApp.GetValueOrDefault(g.Key) + g.Sum(p => p.Cpu) / seconds;
                ioByApp[g.Key] = ioByApp.GetValueOrDefault(g.Key) + g.Sum(p => p.IoRead + p.IoWrite) / seconds;
                gpuByApp[g.Key] = Math.Max(gpuByApp.GetValueOrDefault(g.Key), g.Max(p => p.Gpu));
                memByApp[g.Key] = g.Sum(p => p.Memory);
            }
        }
        if (samples.Count == 0) throw RpcException.Failed("No samples");

        double cpu = samples.Average(s => s.Cpu);
        double maxCore = samples.Average(s => s.Cores.Length > 0 ? s.Cores.Max() : s.Cpu);
        double mem = samples.Average(s => s.MemPercent);
        double faults = samples.Average(s => s.HardFaults);
        double disk = samples.Average(s => s.DiskActive);
        double latency = samples.SelectMany(s => s.Disks).Select(d => d.LatencyMs).DefaultIfEmpty(0).Average();
        double gpu = samples.Average(s => s.Gpu);
        bool throttled = samples.Count(s => s.Throttled) >= 3;
        var last = samples[^1];

        object Culprits<T>(Dictionary<string, T> map, Func<T, double> value, Func<T, LText> text, double min) => map
            .Where(m => value(m.Value) >= min).OrderByDescending(m => value(m.Value)).Take(4)
            .Select(m => new { name = m.Key, value = value(m.Value), text = text(m.Value) }).ToList();

        var factors = new List<object>();
        string bottleneck = "none";
        void Add(string kind, Severity severity, LText title, LText detail, object culprits)
        {
            factors.Add(new { kind, severity, title, detail, culprits });
            if (bottleneck == "none" && severity >= Severity.Warning) bottleneck = kind;
        }

        if (throttled)
            Add("thermal", Severity.Critical, Loc.T("İşlemci ısı nedeniyle kısılıyor", "The processor is being throttled by heat"),
                Loc.T("Şu anki yavaşlığın birincil nedeni bu olabilir: donanım yazılımı işlemci hızını düşürüyor.", "This may be the primary cause right now: the firmware is lowering processor speed."),
                Array.Empty<object>());
        if (mem >= 90 || (mem >= 80 && faults > 300))
            Add("memory", mem >= 94 ? Severity.Critical : Severity.Warning, Loc.T("Bellek dolu, Windows diske sayfalıyor", "Memory is full and Windows is paging to disk"),
                Loc.T("Bellek kullanımı %{0:0}, saniyede {1:0} sayfa diskten okunuyor. En büyük uygulamaları kapatmak en hızlı çözümdür.", "Memory is at {0:0}% with {1:0} pages read from disk per second. Closing the largest apps is the fastest fix.").F(mem, faults),
                Culprits(memByApp, v => v, v => new ByteSize(v).ToString(), 150 << 20));
        if (cpu >= 85)
            Add("cpu", Severity.Critical, Loc.T("İşlemci tam yükte", "The processor is saturated"),
                Loc.T("Tüm çekirdeklerin ortalaması %{0:0}. Aşağıdaki uygulamalar işlemciyi paylaşıyor.", "All cores average {0:0}%. The apps below are sharing the processor.").F(cpu),
                Culprits(cpuByApp, v => v, v => Loc.T("%{0:0}", "{0:0}%").F(v), 3));
        else if (maxCore >= 92 && cpu < 60)
            Add("cpu", Severity.Warning, Loc.T("Tek bir çekirdek sınırında", "A single core is maxed out"),
                Loc.T("Toplam kullanım %{0:0} iken en yoğun çekirdek %{1:0}. Tek iş parçacığıyla çalışan bir uygulama kendi sınırına dayanmış; daha fazla çekirdek ona yardım etmez.", "Total usage is {0:0}% but the busiest core is at {1:0}%. A single-threaded app has hit its own limit; more cores would not help it.").F(cpu, maxCore),
                Culprits(cpuByApp, v => v, v => Loc.T("%{0:0}", "{0:0}%").F(v), 2));
        else if (cpu >= 60)
            Add("cpu", Severity.Info, Loc.T("İşlemci meşgul ama sınırda değil", "The processor is busy but not saturated"),
                Loc.T("Ortalama %{0:0}.", "Average {0:0}%.").F(cpu), Culprits(cpuByApp, v => v, v => Loc.T("%{0:0}", "{0:0}%").F(v), 3));
        if (disk >= 85)
            Add("disk", latency > 30 ? Severity.Critical : Severity.Warning, Loc.T("Disk sürekli meşgul", "The disk is constantly busy"),
                Loc.T("Disk zamanın %{0:0} kadarında çalışıyor, ortalama yanıt {1:0} ms. Diske erişen her şey bu sırada bekler.", "The disk is active {0:0}% of the time with a {1:0} ms average response. Everything that touches the disk waits meanwhile.").F(disk, latency),
                Culprits(ioByApp, v => v, v => Loc.T("{0}/sn", "{0}/s").F(new ByteSize((long)v)), 256 << 10));
        if (gpu >= 95)
            Add("gpu", Severity.Info, Loc.T("Ekran kartı tam yükte", "The graphics card is fully loaded"),
                Loc.T("Bir oyunda bu normaldir ve kare hızını ekran kartının belirlediği anlamına gelir.", "In a game this is normal and means the graphics card sets the frame rate."),
                Culprits(gpuByApp, v => v, v => Loc.T("%{0:0}", "{0:0}%").F(v), 5));
        if (last.OnBattery && PowerPlans.List().FirstOrDefault(p => p.Active)?.Kind == "saver")
            Add("power", Severity.Warning, Loc.T("Pilde ve güç tasarrufu planında", "On battery with the power saver plan"),
                Loc.T("Bu plan işlemci hızını bilerek sınırlar.", "This plan deliberately limits processor speed."), Array.Empty<object>());

        LText verdict = bottleneck switch
        {
            "thermal" => Loc.T("Bilgisayar ısındığı için yavaş.", "The PC is slow because it is hot."),
            "memory" => Loc.T("Bilgisayar belleği dolduğu için yavaş.", "The PC is slow because memory is full."),
            "cpu" => Loc.T("Bilgisayarı şu an işlemci sınırlıyor.", "The processor is the limit right now."),
            "disk" => Loc.T("Bilgisayarı şu an disk bekletiyor.", "The disk is holding the PC back right now."),
            "power" => Loc.T("Güç planı performansı sınırlıyor.", "The power plan is limiting performance."),
            _ => Loc.T("Şu an bir darboğaz yok. Sistem rahat çalışıyor.", "No bottleneck right now. The system has headroom."),
        };

        return new
        {
            verdict, bottleneck, factors, seconds = samples.Count,
            averages = new { cpu, maxCore, mem, faults, disk, latency, gpu, temp = last.Temp, mhz = samples.Average(s => s.CpuMhz) },
            topCpu = Culprits(cpuByApp, v => v, v => Loc.T("%{0:0.0}", "{0:0.0}%").F(v), 0.5),
            topMemory = Culprits(memByApp, v => v, v => new ByteSize(v).ToString(), 50 << 20),
        };
    }

    // ------------------------------------------------------------------ before / after
    private Baseline Capture(string label, JobContext job)
    {
        var samples = new List<LiveSample>();
        for (int i = 0; i < 10; i++)
        {
            job.ThrowIfCancelled();
            job.Report(i / 10.0, Loc.T("Ölçülüyor… {0}/10 sn", "Measuring… {0}/10s").F(i + 1));
            _metrics.Touch();
            Thread.Sleep(1000);
            if (_metrics.Latest is { } s) samples.Add(s);
        }
        var last = samples.LastOrDefault() ?? throw RpcException.Failed("No samples");
        var b = new Baseline
        {
            Id = Guid.NewGuid().ToString("N")[..10], Time = DateTime.UtcNow, Label = label,
            CpuIdle = samples.Average(s => s.Cpu), MemoryUsed = (long)samples.Average(s => s.MemUsed), MemoryPercent = samples.Average(s => s.MemPercent),
            Processes = last.Processes, Threads = last.Threads,
            SystemFree = _system.Volumes().FirstOrDefault(v => v.IsSystem)?.FreeBytes ?? 0,
            HealthScore = (_last ?? Analyze(false)).Score, UptimeSeconds = Environment.TickCount64 / 1000,
        };
        foreach (var (key, read) in _baselineProbes)
        {
            try { b.Extra[key] = read(); } catch { /* optional metric */ }
        }
        var all = _store.Load(Path.Combine("insights", "baselines.json"), () => new List<Baseline>());
        all.Add(b);
        if (all.Count > 40) all.RemoveRange(0, all.Count - 40);
        _store.Save(Path.Combine("insights", "baselines.json"), all);
        return b;
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("insights.summary", _ => _last ?? Analyze(false));
        rpc.On("insights.quick", _ => Analyze(false));
        rpc.On("insights.analyze", _ => _jobs.Start("insights.analyze", Loc.T("Sistem analizi", "System analysis"), job => Analyze(true, job)));
        rpc.On("insights.live", _ => _jobs.Start("insights.live", Loc.T("Canlı teşhis", "Live diagnosis"), LiveDiagnosis));
        rpc.On("insights.history", _ => _store.ReadLines<HealthPoint>(Path.Combine("history", "health.jsonl")).TakeLast(400).ToList());
        rpc.On("insights.ignore", c =>
        {
            string id = c.Str("id");
            bool ignore = c.Bool("ignore", true);
            _settings.Update(s => { s.IgnoredFindings.Remove(id); if (ignore) s.IgnoredFindings.Add(id); });
            return Analyze(false);
        });
        rpc.On("insights.baselines", _ => _store.Load(Path.Combine("insights", "baselines.json"), () => new List<Baseline>()));
        rpc.On("insights.baseline.capture", c =>
        {
            string label = c.Str("label", "");
            return _jobs.Start("insights.baseline", Loc.T("Ölçüm alınıyor", "Capturing a measurement"), job => Capture(label, job));
        });
        rpc.On("insights.baseline.delete", c =>
        {
            string id = c.Str("id");
            var all = _store.Load(Path.Combine("insights", "baselines.json"), () => new List<Baseline>());
            all.RemoveAll(b => b.Id == id);
            _store.Save(Path.Combine("insights", "baselines.json"), all);
            return all;
        });
    }
}
