using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public sealed record LogEvent(DateTime Time, string Log, string Source, int Id, string Level, string Message);

/// <summary>
/// Diagnostics: app crashes and hangs, blue screens, unexpected shutdowns, event log analysis,
/// Windows' reliability index and pending software updates (winget).
/// </summary>
public sealed partial class DiagnosticsService(JobManager jobs, ChangeJournal journal)
{
    // Common bug check codes and what usually causes them.
    private static readonly Dictionary<uint, (string Name, LText Cause)> BugChecks = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", Loc.T("Genellikle hatalı bir sürücü ya da bozuk bellek.", "Usually a faulty driver or bad memory.")),
        [0x1A] = ("MEMORY_MANAGEMENT", Loc.T("Bellek (RAM) sorunu ya da sürücü hatası. Windows Bellek Tanılama'yı çalıştırın.", "A memory (RAM) problem or driver bug. Run Windows Memory Diagnostic.")),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", Loc.T("Bir çekirdek sürücüsü beklenmeyen bir hata verdi.", "A kernel driver raised an unexpected error.")),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", Loc.T("Çoğunlukla ekran kartı ya da güvenlik yazılımı sürücüsü.", "Most often a graphics or security software driver.")),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", Loc.T("Hatalı sürücü, bozuk bellek ya da disk hatası.", "Faulty driver, bad memory or a disk error.")),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", Loc.T("Bir sürücü sistem iş parçacığında çöktü.", "A driver crashed in a system thread.")),
        [0x7F] = ("UNEXPECTED_KERNEL_MODE_TRAP", Loc.T("Donanım hatası ya da hız aşırtma (overclock) kararsızlığı.", "Hardware failure or overclocking instability.")),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", Loc.T("Uyku/uyanma sırasında yanıt vermeyen bir sürücü.", "A driver that did not respond during sleep or wake.")),
        [0xA] = ("IRQL_NOT_LESS_OR_EQUAL", Loc.T("Genellikle hatalı bir sürücü.", "Usually a faulty driver.")),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", Loc.T("Hatalı sürücü; çoğunlukla ağ ya da Wi-Fi sürücüsü.", "Faulty driver; most often network or Wi-Fi.")),
        [0xEF] = ("CRITICAL_PROCESS_DIED", Loc.T("Kritik bir Windows işlemi sonlandı; sistem dosyası bozulması ya da disk hatası olabilir.", "A critical Windows process died; possibly system file corruption or a disk error.")),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", Loc.T("Donanım hatası: aşırı ısınma, hız aşırtma, güç kaynağı ya da işlemci/bellek arızası.", "Hardware error: overheating, overclocking, power supply or CPU/memory failure.")),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", Loc.T("Bir sürücü çok uzun süre yanıt vermedi; çoğunlukla SSD denetleyicisi ya da yonga seti sürücüsü.", "A driver took too long to respond; often the SSD controller or chipset driver.")),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", Loc.T("Sürücü ya da bellek bozulması.", "Driver or memory corruption.")),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", Loc.T("Disk ya da depolama sürücüsü sorunu.", "Disk or storage driver problem.")),
        [0x1000007E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED_M", Loc.T("Bir sürücü sistem iş parçacığında çöktü.", "A driver crashed in a system thread.")),
        [0x116] = ("VIDEO_TDR_FAILURE", Loc.T("Ekran kartı sürücüsü yanıt vermeyi bıraktı ve kurtarılamadı. Sürücüyü güncelleyin ya da temiz kurun; sıcaklığı denetleyin.", "The graphics driver stopped responding and could not recover. Update or clean-install the driver; check temperatures.")),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", Loc.T("Ekran kartı zaman aşımına uğradı.", "The graphics card timed out.")),
        [0xC2] = ("BAD_POOL_CALLER", Loc.T("Hatalı sürücü.", "Faulty driver.")),
        [0x19] = ("BAD_POOL_HEADER", Loc.T("Sürücü ya da bellek bozulması.", "Driver or memory corruption.")),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", Loc.T("Disk bağlantısı ya da disk hatası.", "Disk connection or disk failure.")),
    };

    private static IEnumerable<EventRecord> Read(string log, string xpath, int max)
    {
        EventLogReader reader;
        try { reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true }); }
        catch { yield break; }
        using (reader)
            for (int i = 0; i < max; i++)
            {
                EventRecord? e;
                try { e = reader.ReadEvent(); } catch { yield break; }
                if (e is null) yield break;
                yield return e;
            }
    }

    private static string Since(int days) => $"TimeCreated[timediff(@SystemTime) <= {(long)days * 86400000}]";

    private static List<string> Props(EventRecord e)
    {
        try { return [.. e.Properties.Select(p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "")]; } catch { return []; }
    }

    private static string Message(EventRecord e)
    {
        try { return (e.FormatDescription() ?? "").Trim(); } catch { return ""; }
    }

    // ------------------------------------------------------------------ app crashes and hangs
    private sealed record Crash(DateTime Time, string App, string Kind, string? Module, string? Code, string? Path, string? Version);

    private static List<Crash> Crashes(int days)
    {
        var list = new List<Crash>();
        // Other programs reuse event ids 1000/1002 for their own messages: only Windows' crash and hang reporters count.
        foreach (var e in Read("Application", $"*[System[Provider[@Name='Application Error' or @Name='Application Hang'] and (EventID=1000 or EventID=1002) and {Since(days)}]]", 600))
            using (e)
            {
                var p = Props(e);
                if (p.Count < 1) continue;
                bool hang = e.Id == 1002;
                list.Add(new Crash(e.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow, p[0], hang ? "hang" : "crash",
                    hang ? null : p.ElementAtOrDefault(3), hang ? null : p.ElementAtOrDefault(6), hang ? p.ElementAtOrDefault(5) : p.ElementAtOrDefault(10), p.ElementAtOrDefault(1)));
            }
        return list;
    }

    private static readonly Dictionary<string, LText> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c0000005"] = Loc.T("Erişim ihlali: program geçersiz bir bellek adresine erişti (yazılım hatası, bozuk eklenti ya da bellek sorunu).", "Access violation: the program touched an invalid memory address (software bug, broken plug-in or memory problem)."),
        ["c0000409"] = Loc.T("Yığın taşması koruması: program kendini güvenlik nedeniyle sonlandırdı.", "Stack buffer overrun protection: the program terminated itself for safety."),
        ["c00000fd"] = Loc.T("Yığın taşması: sonsuz özyineleme gibi bir yazılım hatası.", "Stack overflow: a software bug such as infinite recursion."),
        ["e0434352"] = Loc.T(".NET uygulamasında yakalanmamış istisna.", "Unhandled exception in a .NET application."),
        ["c0000374"] = Loc.T("Bellek yığını bozulması.", "Heap corruption."),
        ["80000003"] = Loc.T("Kesme noktası: program kasıtlı olarak durdu (çoğunlukla dahili bir doğrulama hatası).", "Breakpoint: the program stopped on purpose (usually an internal assertion)."),
        ["c06d007e"] = Loc.T("Gerekli bir DLL bulunamadı; programı onarın ya da yeniden kurun.", "A required DLL was not found; repair or reinstall the program."),
        ["c0000142"] = Loc.T("Program başlatılamadı (DLL başlatma hatası).", "The program failed to start (DLL initialization failure)."),
    };

    private object AppHealth(int days)
    {
        var crashes = Crashes(days);
        var apps = crashes.GroupBy(c => c.App, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var modules = g.Where(c => c.Module is not null).GroupBy(c => c.Module!, StringComparer.OrdinalIgnoreCase).OrderByDescending(m => m.Count()).Take(3)
                .Select(m => new { module = m.Key, count = m.Count() }).ToList();
            string? code = g.Where(c => c.Code is not null).GroupBy(c => c.Code!.Replace("0x", "").ToLowerInvariant()).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key;
            string? topModule = modules.FirstOrDefault()?.module;
            LText hint = topModule is not null && !topModule.Equals(g.Key, StringComparison.OrdinalIgnoreCase) && g.Count(c => c.Module == topModule) * 2 > g.Count(c => c.Kind == "crash")
                ? Loc.T("Çökmelerin çoğu {0} modülünde. Bu bir eklenti, sürücü ya da üçüncü taraf DLL olabilir; programın kendisi değil.", "Most crashes happen in {0}. It may be a plug-in, driver or third-party DLL rather than the program itself.").F(topModule)
                : code is not null && Codes.TryGetValue(code, out var known) ? known : Loc.T("", "");
            return new
            {
                app = g.Key, path = g.Select(c => c.Path).FirstOrDefault(p => p is not null), version = g.First().Version,
                crashes = g.Count(c => c.Kind == "crash"), hangs = g.Count(c => c.Kind == "hang"), last = g.Max(c => c.Time),
                code, modules, hint,
                timeline = g.GroupBy(c => c.Time.ToLocalTime().Date).Select(d => new { day = d.Key, count = d.Count() }).OrderBy(d => d.day).ToList(),
            };
        }).OrderByDescending(a => a.crashes + a.hangs).ToList();
        return new { apps, total = crashes.Count, days };
    }

    // ------------------------------------------------------------------ blue screens and shutdowns
    [GeneratedRegex(@"0x([0-9a-fA-F]{8})")]
    private static partial Regex HexCode();

    private object BlueScreens()
    {
        var list = new List<object>();
        foreach (var e in Read("System", "*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001]]", 50))
            using (e)
            {
                var p = Props(e);
                string text = string.Join(" ", p) + " " + Message(e);
                var m = HexCode().Match(text);
                uint code = m.Success ? Convert.ToUInt32(m.Groups[1].Value, 16) : 0;
                BugChecks.TryGetValue(code, out var info);
                string? dump = p.FirstOrDefault(x => x.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)) ?? Regex.Match(text, @"[A-Za-z]:\\[^\s]+\.dmp").Value;
                list.Add(new
                {
                    time = e.TimeCreated?.ToUniversalTime(), code = code == 0 ? null : $"0x{code:X8}", name = info.Name, cause = info.Name is null ? (LText?)null : info.Cause,
                    dump = string.IsNullOrEmpty(dump) ? null : dump, dumpExists = !string.IsNullOrEmpty(dump) && File.Exists(dump),
                });
            }

        var unexpected = new List<object>();
        foreach (var e in Read("System", $"*[System[(EventID=41 or EventID=6008) and {Since(90)}]]", 60))
            using (e)
            {
                if (e.Id == 41 && e.ProviderName != "Microsoft-Windows-Kernel-Power") continue;
                var p = Props(e);
                // Event 41 with BugcheckCode 0 and PowerButtonTimestamp 0 means power was cut or the PC froze.
                string kind = e.Id == 6008 ? "unexpected" : p.ElementAtOrDefault(0) is { } bc && bc != "0" ? "bugcheck" : p.ElementAtOrDefault(4) is { } pb && pb != "0" ? "button" : "power";
                unexpected.Add(new { time = e.TimeCreated?.ToUniversalTime(), kind });
            }

        var dumps = new List<object>();
        foreach (var dir in new[] { Path.Combine(Paths.Windows, "Minidump") })
            try { foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.dmp")) dumps.Add(new { path = f.FullName, bytes = f.Length, time = f.LastWriteTimeUtc }); } catch { /* admin only */ }
        bool memoryDump = File.Exists(Path.Combine(Paths.Windows, "MEMORY.DMP"));
        return new { bluescreens = list, unexpected = unexpected.Take(40).ToList(), dumps, memoryDump, canReadDumps = Elevation.IsElevated };
    }

    // ------------------------------------------------------------------ event log
    private object EventLogSummary(int days, string level)
    {
        string levels = level == "critical" ? "Level=1" : level == "error" ? "(Level=1 or Level=2)" : "(Level=1 or Level=2 or Level=3)";
        var events = new List<LogEvent>();
        foreach (var log in new[] { "System", "Application" })
            foreach (var e in Read(log, $"*[System[{levels} and {Since(days)}]]", 1500))
                using (e)
                    events.Add(new LogEvent(e.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow, log, e.ProviderName ?? "?", e.Id,
                        e.Level switch { 1 => "critical", 2 => "error", 3 => "warning", _ => "info" }, ""));

        var groups = events.GroupBy(e => (e.Log, e.Source, e.Id)).Select(g => new
        {
            log = g.Key.Log, source = g.Key.Source, id = g.Key.Id, level = g.Min(x => x.Level == "critical" ? 0 : x.Level == "error" ? 1 : 2) switch { 0 => "critical", 1 => "error", _ => "warning" },
            count = g.Count(), first = g.Min(x => x.Time), last = g.Max(x => x.Time),
            daily = g.GroupBy(x => x.Time.ToLocalTime().Date).Count(),
        }).OrderByDescending(g => g.count).Take(80).ToList();
        var perDay = events.GroupBy(e => e.Time.ToLocalTime().Date).Select(d => new { day = d.Key, errors = d.Count(x => x.Level is "critical" or "error"), warnings = d.Count(x => x.Level == "warning") }).OrderBy(d => d.day).ToList();
        return new { groups, perDay, total = events.Count, days };
    }

    private static object EventSample(string log, string source, int id)
    {
        if (log is not ("System" or "Application")) throw RpcException.Invalid("Unknown log");
        string safe = source.Replace("'", "");
        var samples = Read(log, $"*[System[Provider[@Name='{safe}'] and EventID={id}]]", 5).Select(e =>
        {
            using (e) return new { time = e.TimeCreated?.ToUniversalTime(), message = Message(e) is { Length: > 0 } m ? (m.Length > 3000 ? m[..3000] : m) : string.Join(" | ", Props(e)) };
        }).ToList();
        return new { samples, explain = Explain(source, id) };
    }

    /// <summary>Plain-language notes for events people ask about most.</summary>
    private static LText? Explain(string source, int id) => (source, id) switch
    {
        ("Microsoft-Windows-DistributedCOM", 10016) => Loc.T("Zararsız: Windows bileşenlerinin izin kayıtlarıyla ilgili bilinen bir uyarı. Microsoft yoksayılmasını önerir.", "Harmless: a known warning about permission records of Windows components. Microsoft recommends ignoring it."),
        ("Microsoft-Windows-Kernel-Power", 41) => Loc.T("Bilgisayar düzgün kapatılmadan yeniden başladı: güç kesintisi, donma ya da mavi ekran.", "The PC restarted without a clean shutdown: power loss, a freeze or a blue screen."),
        ("EventLog", 6008) => Loc.T("Bir önceki kapanış beklenmedikti.", "The previous shutdown was unexpected."),
        ("disk", 7) or ("disk", 11) or ("disk", 51) => Loc.T("Disk okuma/yazma hatası. Tekrarlıyorsa diski yedekleyin ve sağlığını kontrol edin.", "Disk read/write error. If it repeats, back up the disk and check its health."),
        ("Ntfs", 55) or ("Ntfs", 98) => Loc.T("Dosya sistemi tutarsızlığı. Bir sonraki açılışta chkdsk çalıştırın.", "File system inconsistency. Run chkdsk at the next boot."),
        ("WHEA-Logger", _) or ("Microsoft-Windows-WHEA-Logger", _) => Loc.T("Donanımın kendisinin bildirdiği hata (işlemci, bellek, PCIe). Sık tekrarlıyorsa donanım ya da hız aşırtma sorunu vardır.", "An error reported by the hardware itself (CPU, memory, PCIe). Frequent repeats point to hardware or overclocking problems."),
        ("Display", 4101) or ("nvlddmkm", _) or ("amdkmdag", _) => Loc.T("Ekran kartı sürücüsü yanıt vermeyi bıraktı ve yeniden başlatıldı (TDR).", "The graphics driver stopped responding and was restarted (TDR)."),
        ("Microsoft-Windows-Time-Service", _) => Loc.T("Saat eşitlemesi sorunu; genellikle zararsızdır.", "Clock synchronization issue; usually harmless."),
        ("Microsoft-Windows-WindowsUpdateClient", 20) => Loc.T("Bir güncelleme kurulamadı.", "An update failed to install."),
        ("Microsoft-Windows-DistributedCOM", 10010) or ("Microsoft-Windows-DistributedCOM", 10005) => Loc.T("Bir Windows bileşeni beklenen sürede başlamadı. Arada bir görülmesi zararsızdır; bir uygulamanın açılmamasıyla aynı anda oluyorsa o uygulamayı onarın.", "A Windows component did not start in time. Occasional occurrences are harmless; if it coincides with an app failing to open, repair that app."),
        ("Service Control Manager", 7023) or ("Service Control Manager", 7024) or ("Service Control Manager", 7000) => Loc.T("Bir hizmet başlatılamadı ya da hatayla durdu. Hangi hizmet olduğu aşağıdaki iletide yazar.", "A service failed to start or stopped with an error. The message below names the service."),
        (".NET Runtime", 1026) or (".NET Runtime", 1022) or (".NET Runtime", 1023) => Loc.T(".NET tabanlı bir uygulama hatayla kapandı ya da başlatılamadı.", "A .NET-based application crashed or failed to start."),
        _ => (LText?)null,
    };

    // ------------------------------------------------------------------ reliability
    private static object Reliability()
    {
        var rows = Wmi.Query("SELECT SystemStabilityIndex, TimeGenerated FROM Win32_ReliabilityStabilityMetrics");
        var points = rows.Select(r => new { t = r.Date("TimeGenerated"), index = r.D("SystemStabilityIndex") }).Where(p => p.t is not null)
            .GroupBy(p => p.t!.Value.ToLocalTime().Date).Select(g => new { day = g.Key, index = g.OrderByDescending(x => x.t).First().index }).OrderBy(p => p.day).TakeLast(60).ToList();
        var records = Wmi.Query("SELECT SourceName, ProductName, EventIdentifier, TimeGenerated, Message FROM Win32_ReliabilityRecords")
            .Select(r => new { time = r.Date("TimeGenerated"), source = r.S("SourceName"), product = r.S("ProductName"), id = r.I("EventIdentifier"), message = r.S("Message") is { Length: > 300 } m ? m[..300] + "…" : r.S("Message") })
            .Where(r => r.time is not null).OrderByDescending(r => r.time).Take(120).ToList();
        return new { points, records, current = points.LastOrDefault()?.index };
    }

    // ------------------------------------------------------------------ software updates (winget)
    private static string? Winget()
    {
        string path = Path.Combine(Paths.Local, @"Microsoft\WindowsApps\winget.exe");
        return File.Exists(path) ? path : null;
    }

    private object Upgrades(JobContext job)
    {
        string winget = Winget() ?? throw RpcException.Unsupported(Loc.S("Bu bilgisayarda winget (Uygulama Yükleyicisi) yok. Microsoft Store'dan \"Uygulama Yükleyicisi\"ni kurun.", "winget (App Installer) is not available. Install \"App Installer\" from the Microsoft Store."));
        job.Report(-1, Loc.T("Güncellemeler denetleniyor…", "Checking for updates…"));
        var r = Proc.Run(winget, "upgrade --include-unknown --accept-source-agreements --disable-interactivity", 180000, System.Text.Encoding.UTF8);
        return new { items = ParseWinget(r.StdOut) };
    }

    /// <summary>Parses winget's table: column positions come from the header line, so it works in any display language.</summary>
    internal static List<object> ParseWinget(string output)
    {
        var lines = output.Replace("\r", "").Split('\n').Select(l => { int cut = l.LastIndexOf('\b'); return cut >= 0 ? l[(cut + 1)..] : l; }).ToList();
        int sep = lines.FindIndex(l => l.Length > 20 && l.Trim().All(c => c == '-'));
        var items = new List<object>();
        if (sep < 1) return items;
        string header = lines[sep - 1];
        var starts = new List<int> { 0 };
        for (int i = 1; i < header.Length; i++) if (header[i - 1] == ' ' && header[i] != ' ') starts.Add(i);
        if (starts.Count < 4) return items;
        // winget pads with display width, so wide characters shift columns; compute by text elements.
        for (int li = sep + 1; li < lines.Count; li++)
        {
            string line = lines[li];
            if (line.Trim().Length == 0 || !line.Contains("  ")) continue;
            string Col(int c) => starts[c] >= line.Length ? "" : (c + 1 < starts.Count ? line[starts[c]..Math.Min(line.Length, starts[c + 1])] : line[starts[c]..]).Trim();
            string id = Col(1);
            if (id.Length == 0 || id.Contains(' ')) continue;
            items.Add(new { name = Col(0), id, version = Col(2), available = Col(3), source = starts.Count > 4 ? Col(4) : "" });
        }
        return items;
    }

    private object Upgrade(string[] ids, JobContext job)
    {
        string winget = Winget() ?? throw RpcException.Unsupported("winget");
        var results = new List<object>();
        using var scope = journal.Begin("tools", Loc.T("{0} uygulama güncellendi", "{0} apps updated").F(ids.Length));
        for (int i = 0; i < ids.Length; i++)
        {
            string id = ids[i];
            if (!Regex.IsMatch(id, @"^[\w.\-+]+$")) { results.Add(new { id, ok = false, output = "invalid id" }); continue; }
            job.ThrowIfCancelled();
            job.Report(i / (double)ids.Length, Loc.T("Güncelleniyor: {0}", "Updating: {0}").F(id));
            scope.Note("app.upgrade", id);
            if (scope.DryRun) { results.Add(new { id, ok = true, output = "simulated" }); continue; }
            var r = Proc.Run(winget, $"upgrade --id {id} --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity", 30 * 60 * 1000, System.Text.Encoding.UTF8);
            results.Add(new { id, ok = r.Ok, output = r.Text.Length > 600 ? r.Text[^600..] : r.Text });
        }
        return new { results, simulated = scope.DryRun };
    }

    public IEnumerable<Finding> Findings()
    {
        var crashes = Crashes(14).GroupBy(c => c.App, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 3).OrderByDescending(g => g.Count()).ToList();
        if (crashes.Count > 0)
            yield return new Finding
            {
                Id = "diagnostics.crashes", Group = "stability", Severity = crashes.Sum(g => g.Count()) >= 10 ? Severity.Warning : Severity.Info, Penalty = 6,
                Title = Loc.T("{0} uygulama son 14 günde tekrar tekrar çöktü", "{0} apps crashed repeatedly in the last 14 days").F(crashes.Count),
                Detail = Loc.T("Tekrarlayan çökmelerin çoğu bir eklenti, eski bir sürüm ya da bozuk kurulumdan kaynaklanır. Tanılama sayfası hangi modülün sorumlu olduğunu gösterir.", "Most repeated crashes come from a plug-in, an old version or a broken install. The Diagnostics page shows which module is responsible."),
            }.Ev(Loc.T("Uygulamalar", "Apps"), string.Join(", ", crashes.Take(4).Select(g => $"{g.Key} ({g.Count()})"))).Go(Loc.T("Çökme analizi", "Crash analysis"), "diagnostics/crashes");

        int bsod = Read("System", $"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001 and {Since(30)}]]", 20).Count();
        if (bsod > 0)
            yield return new Finding
            {
                Id = "diagnostics.bsod", Group = "stability", Severity = bsod >= 2 ? Severity.Critical : Severity.Warning, Penalty = bsod >= 2 ? 30 : 15,
                Title = Loc.T("Son 30 günde {0} mavi ekran hatası", "{0} blue screen(s) in the last 30 days").F(bsod),
                Detail = Loc.T("Mavi ekranlar neredeyse her zaman bir sürücü ya da donanım sorunudur. Tanılama sayfası hata kodunu ve olası nedenini gösterir.", "Blue screens are almost always a driver or hardware problem. The Diagnostics page shows the error code and likely cause."),
            }.Go(Loc.T("Mavi ekran analizi", "Blue screen analysis"), "diagnostics/bsod");
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("diag.apps", c => AppHealth(Math.Clamp(c.Int("days", 30), 1, 90)));
        rpc.On("diag.bsod", _ => BlueScreens());
        rpc.On("diag.events", c => EventLogSummary(Math.Clamp(c.Int("days", 7), 1, 30), c.Str("level", "error")));
        rpc.On("diag.eventSample", c => EventSample(c.Str("log"), c.Str("source"), c.Int("id")));
        rpc.On("diag.reliability", _ => Reliability());
        rpc.On("diag.updates", _ => new { history = SecurityService.UpdateHistory(80)?.Select(h => new { date = h.Date, result = h.Result, title = h.Title }).ToList() });
        rpc.On("diag.upgrades", _ => jobs.Start("diag.upgrades", Loc.T("Uygulama güncellemeleri denetleniyor", "Checking app updates"), Upgrades));
        rpc.On("diag.upgrade", c => { var ids = c.StrArr("ids"); return jobs.Start("diag.upgrade", Loc.T("Uygulamalar güncelleniyor", "Updating apps"), job => Upgrade(ids, job)); });
        rpc.Do("diag.memoryTest", _ => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Proc.Sys("MdSched.exe")) { UseShellExecute = true }));
        rpc.Do("diag.reliabilityMonitor", _ => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Proc.Sys("perfmon.exe"), "/rel") { UseShellExecute = true }));
    }
}
