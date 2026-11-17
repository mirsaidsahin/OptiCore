using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.Win32;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public sealed class StartupEntry
{
    public string Id { get; set; } = "";
    /// <summary>registry | folder | task | service | uwp</summary>
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string? Path { get; set; }
    public string? Publisher { get; set; }
    public string Location { get; set; } = "";
    /// <summary>user | machine</summary>
    public string Scope { get; set; } = "user";
    public bool Enabled { get; set; }
    public bool RequiresAdmin { get; set; }
    /// <summary>high | medium | low | unknown</summary>
    public string Impact { get; set; } = "unknown";
    /// <summary>windows (Windows' own boot trace) | opticore (measured after logon) | null</summary>
    public string? ImpactSource { get; set; }
    public double? CpuMs { get; set; }
    public long? DiskBytes { get; set; }
    public bool FileMissing { get; set; }
    public bool Microsoft { get; set; }
    public string? Signer { get; set; }
    public bool Signed { get; set; }
    /// <summary>Reasons this entry deserves a second look (see <see cref="Heuristics"/>).</summary>
    public List<LText> Flags { get; set; } = [];
    public string? StartMode { get; set; }
    public string? State { get; set; }
    public string? Description { get; set; }
    public string? Delayed { get; set; }

    // Where the enabled/disabled switch lives (not serialized to the UI).
    [System.Text.Json.Serialization.JsonIgnore] public string? ApprovedKey { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? ApprovedName { get; set; }
}

/// <summary>Signals that make an auto-start entry or a process worth a closer look. Each returns a reason, never a verdict.</summary>
public static class Heuristics
{
    private static readonly string[] ScriptHosts = ["powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "cmd.exe", "cmd /", "msiexec", "bitsadmin", "certutil", "curl", "schtasks"];
    private static readonly string[] SystemNames = ["svchost.exe", "csrss.exe", "lsass.exe", "services.exe", "winlogon.exe", "explorer.exe", "smss.exe", "dwm.exe", "taskhostw.exe", "spoolsv.exe", "conhost.exe", "ctfmon.exe", "sihost.exe"];

    public static List<LText> Flags(string? command, string? path)
    {
        var flags = new List<LText>();
        string cmd = (command ?? "").ToLowerInvariant();
        string p = (path ?? "").ToLowerInvariant();

        bool powershell = cmd.Contains("powershell") || cmd.Contains("pwsh");
        if (powershell && (cmd.Contains(" -enc") || cmd.Contains(" -e ") || cmd.Contains(" -ec ") || cmd.Contains("frombase64string") || cmd.Contains("-w hidden") || cmd.Contains("-windowstyle hidden") || cmd.Contains("downloadstring") || cmd.Contains("iex ")))
            flags.Add(Loc.T("Gizli ya da kodlanmış PowerShell komutu çalıştırıyor", "Runs a hidden or encoded PowerShell command"));
        else if (ScriptHosts.Any(h => cmd.Contains(h)) && (cmd.Contains("http://") || cmd.Contains("https://") || cmd.Contains("javascript:") || cmd.Contains("vbscript:")))
            flags.Add(Loc.T("Bir sistem aracı üzerinden internetten içerik çalıştırıyor", "Uses a system tool to run content from the internet"));

        if (p.Length > 0)
        {
            string temp = System.IO.Path.GetTempPath().TrimEnd('\\').ToLowerInvariant();
            if (p.StartsWith(temp + "\\") || p.Contains(@"\appdata\local\temp\"))
                flags.Add(Loc.T("Geçici klasörden çalışıyor", "Runs from the temporary folder"));
            else if (p.Contains(@"\downloads\"))
                flags.Add(Loc.T("İndirilenler klasöründen çalışıyor", "Runs from the Downloads folder"));
            else if (p.StartsWith(@"c:\users\public\") || p.StartsWith(@"c:\programdata\") && p.Count(ch => ch == '\\') <= 2)
                flags.Add(Loc.T("Alışılmadık, herkesin yazabildiği bir konumdan çalışıyor", "Runs from an unusual, world-writable location"));

            string file = System.IO.Path.GetFileName(p);
            if (SystemNames.Contains(file) && !p.StartsWith(Paths.Windows.ToLowerInvariant() + "\\"))
                flags.Add(Loc.T("Bir Windows bileşeninin adını taşıyor ama Windows klasöründe değil", "Carries the name of a Windows component but is not in the Windows folder"));
            if (file.Count(ch => ch == '.') >= 2 && new[] { ".pdf.exe", ".doc.exe", ".docx.exe", ".jpg.exe", ".png.exe", ".txt.exe", ".mp4.exe" }.Any(file.EndsWith))
                flags.Add(Loc.T("Çift uzantıyla belge gibi görünmeye çalışıyor", "Uses a double extension to look like a document"));
        }
        return flags;
    }
}

public sealed class BootRecord
{
    public DateTime Time { get; set; }
    /// <summary>Total boot time in milliseconds as measured by Windows.</summary>
    public long TotalMs { get; set; }
    public long MainPathMs { get; set; }
    public long PostBootMs { get; set; }
}

public sealed class StartupService
{
    private const string RunUser = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunMachine = @"HKLM\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedUser = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private const string ApprovedMachine = @"HKLM\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private const string UwpRoot = @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    private readonly ChangeJournal _journal;
    private readonly DataStore _store;
    private readonly NotificationCenter _notices;
    private readonly SettingsService _settings;
    private readonly ProcessSampler _processes;
    private readonly object _gate = new();
    private List<StartupEntry>? _cache;
    private long _cacheAt;
    private Timer? _guard;

    public StartupService(ChangeJournal journal, DataStore store, NotificationCenter notices, SettingsService settings, ProcessSampler processes)
    {
        _journal = journal; _store = store; _notices = notices; _settings = settings; _processes = processes;
        journal.RegisterUndo("startup.uwp", e =>
        {
            using var key = Reg.Create(e.Target);
            key.SetValue("State", int.Parse(e.Before["state"]!, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
        });
    }

    public void Start()
    {
        _guard = new Timer(_ => { try { Guard(); } catch (Exception e) { Log.Warn("startup: guard", e); } }, null, TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(15));
        // Shortly after logon the CPU time each startup app has burned is its startup cost: measure it once.
        long uptime = Environment.TickCount64 / 1000;
        if (uptime < 400) _ = Task.Delay(TimeSpan.FromSeconds(Math.Max(5, 150 - uptime))).ContinueWith(_ => { try { MeasureImpact(); } catch (Exception e) { Log.Warn("startup: measure", e); } });
    }

    // ------------------------------------------------------------------ enumeration
    public List<StartupEntry> List(bool refresh = false)
    {
        lock (_gate)
        {
            if (!refresh && _cache is not null && Environment.TickCount64 - _cacheAt < 20000) return _cache;
            var list = new List<StartupEntry>();
            Safe(() => ReadRun(list, RunUser, RegistryView.Registry64, ApprovedUser + "Run", "user"));
            Safe(() => ReadRun(list, RunMachine, RegistryView.Registry64, ApprovedMachine + "Run", "machine"));
            Safe(() => ReadRun(list, RunMachine, RegistryView.Registry32, ApprovedMachine + "Run32", "machine"));
            Safe(() => ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), ApprovedUser + "StartupFolder", "user"));
            Safe(() => ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), ApprovedMachine + "StartupFolder", "machine"));
            Safe(() => ReadUwp(list));
            Safe(() => ReadTasks(list));
            Safe(() => ReadServices(list));
            ApplyImpact(list);
            foreach (var e in list) Enrich(e);
            _cache = list;
            _cacheAt = Environment.TickCount64;
            return list;
        }
    }

    private static void Safe(Action a)
    {
        try { a(); } catch (Exception e) { Log.Warn("startup: source failed", e); }
    }

    private static bool IsApproved(string approvedKey, string name)
    {
        // Task Manager's own switch: a 12-byte value whose first byte is even when enabled, odd when disabled.
        return Reg.Get(approvedKey, name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    private static void ReadRun(List<StartupEntry> list, string key, RegistryView view, string approvedKey, string scope)
    {
        using var k = Reg.Open(key, false, view);
        if (k is null) return;
        foreach (var name in k.GetValueNames())
        {
            if (string.IsNullOrEmpty(name) || k.GetValue(name) is not string command || command.Length == 0) continue;
            list.Add(new StartupEntry
            {
                Id = $"run:{scope}:{(view == RegistryView.Registry32 ? "32" : "64")}:{name}", Kind = "registry", Name = name, Command = command, Scope = scope,
                Path = Paths.ExeFromCommand(command), Location = key + (view == RegistryView.Registry32 ? " (32-bit)" : ""),
                Enabled = IsApproved(approvedKey, name), RequiresAdmin = scope == "machine", ApprovedKey = approvedKey, ApprovedName = name,
            });
        }
    }

    private static void ReadFolder(List<StartupEntry> list, string folder, string approvedKey, string scope)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            string fileName = System.IO.Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            string target = file, args = "";
            if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                    dynamic link = shell.CreateShortcut(file);
                    target = (string)link.TargetPath;
                    args = (string)link.Arguments;
                }
                catch { /* unreadable shortcut */ }
            }
            list.Add(new StartupEntry
            {
                Id = $"folder:{scope}:{fileName}", Kind = "folder", Name = System.IO.Path.GetFileNameWithoutExtension(file), Scope = scope,
                Command = string.IsNullOrEmpty(args) ? target : $"\"{target}\" {args}", Path = string.IsNullOrEmpty(target) ? file : target, Location = folder,
                Enabled = IsApproved(approvedKey, fileName), RequiresAdmin = scope == "machine", ApprovedKey = approvedKey, ApprovedName = fileName,
            });
        }
    }

    private static void ReadUwp(List<StartupEntry> list)
    {
        foreach (var family in Reg.SubKeys(UwpRoot))
            foreach (var task in Reg.SubKeys(UwpRoot + "\\" + family))
            {
                string key = $"{UwpRoot}\\{family}\\{task}";
                if (Reg.GetInt(key, "State") is not int state) continue;
                string name = family.Split('_')[0];
                // Friendly name from the package repository, when the package is registered for this user.
                const string repo = @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
                string? full = Reg.SubKeys(repo).FirstOrDefault(p => p.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase));
                string? display = full is null ? null : ShellOps.PackageString(full, Reg.GetStr(repo + "\\" + full, "DisplayName"));
                list.Add(new StartupEntry
                {
                    Id = $"uwp:{family}:{task}", Kind = "uwp", Name = string.IsNullOrWhiteSpace(display) ? ShellOps.PrettyPackageName(name) : display!, Command = family, Location = "Microsoft Store",
                    Scope = "user", Enabled = state is 2 or 4, RequiresAdmin = false, ApprovedKey = key, Publisher = "Microsoft Store",
                    Microsoft = name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase),
                });
            }
    }

    private static void ReadTasks(List<StartupEntry> list)
    {
        dynamic service = TaskSched.Connect();
        var stack = new Stack<dynamic>();
        stack.Push(service.GetFolder("\\"));
        while (stack.Count > 0)
        {
            dynamic folder = stack.Pop();
            string folderPath = (string)folder.Path;
            // Windows ships hundreds of maintenance tasks under \Microsoft; they are not startup apps.
            if (folderPath.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
            try { foreach (dynamic sub in folder.GetFolders(0)) stack.Push(sub); } catch { /* access denied */ }
            dynamic tasks;
            try { tasks = folder.GetTasks(1); } catch { continue; }
            foreach (dynamic task in tasks)
            {
                try
                {
                    dynamic def = task.Definition;
                    string? trigger = null;
                    string? delay = null;
                    foreach (dynamic t in def.Triggers)
                    {
                        int type = (int)t.Type;
                        if (type is 8 or 9) { trigger = type == 8 ? "boot" : "logon"; try { delay = (string)t.Delay; } catch { /* no delay property */ } break; }
                    }
                    if (trigger is null) continue;
                    string command = "";
                    foreach (dynamic a in def.Actions)
                        if ((int)a.Type == 0) { command = ((string)a.Path + " " + (string)(a.Arguments ?? "")).Trim(); break; }
                    string path = (string)task.Path;
                    list.Add(new StartupEntry
                    {
                        Id = "task:" + path, Kind = "task", Name = (string)task.Name, Command = command, Path = Paths.ExeFromCommand(command),
                        Location = folderPath, Scope = trigger == "boot" ? "machine" : "user", Enabled = (bool)task.Enabled,
                        RequiresAdmin = true, Publisher = (string?)def.RegistrationInfo.Author, Delayed = string.IsNullOrEmpty(delay) ? null : delay,
                        Description = trigger == "boot" ? "boot" : "logon",
                    });
                }
                catch { /* task we may not read */ }
            }
        }
    }

    private static void ReadServices(List<StartupEntry> list)
    {
        foreach (var row in Wmi.Query("SELECT Name, DisplayName, StartMode, State, PathName, DelayedAutoStart, Description FROM Win32_Service WHERE StartMode = 'Auto'"))
        {
            string command = row.S("PathName");
            string? path = Paths.ExeFromCommand(command);
            if (path is not null && path.EndsWith("svchost.exe", StringComparison.OrdinalIgnoreCase)) continue; // shared Windows service hosts
            list.Add(new StartupEntry
            {
                Id = "service:" + row.S("Name"), Kind = "service", Name = row.S("DisplayName"), Command = command, Path = path, Location = row.S("Name"),
                Scope = "machine", Enabled = true, RequiresAdmin = true, State = row.S("State").ToLowerInvariant(),
                StartMode = row.B("DelayedAutoStart") ? "delayed-auto" : "auto", Description = row.S("Description"),
            });
        }
    }

    private static void Enrich(StartupEntry e)
    {
        if (e.Kind == "uwp") return;
        string? path = e.Path;
        if (!string.IsNullOrEmpty(path) && !System.IO.Path.IsPathRooted(path))
        {
            // Bare names such as "ctfmon.exe" resolve through System32.
            string candidate = System.IO.Path.Combine(Environment.SystemDirectory, path);
            if (File.Exists(candidate)) e.Path = path = candidate;
        }
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { e.FileMissing = !string.IsNullOrEmpty(path) && System.IO.Path.IsPathRooted(path); e.Flags = Heuristics.Flags(e.Command, null); return; }
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            e.Publisher ??= string.IsNullOrWhiteSpace(v.CompanyName) ? null : v.CompanyName.Trim();
            if (e.Kind != "service" && !string.IsNullOrWhiteSpace(v.FileDescription) && e.Name.Length < 3) e.Name = v.FileDescription;
        }
        catch { /* no version info */ }
        var sig = Signature.Check(path);
        e.Signed = sig.Signed;
        e.Signer = sig.Signer;
        e.Microsoft = (e.Publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ?? false) || (sig.Signer?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ?? false);
        e.Flags = Heuristics.Flags(e.Command, path);
        bool inWindows = path.StartsWith(Paths.Windows, StringComparison.OrdinalIgnoreCase);
        if (!sig.Signed && !inWindows && e.Flags.Count > 0)
            e.Flags.Add(Loc.T("Dijital imzası yok", "Has no digital signature"));
    }

    // ------------------------------------------------------------------ impact
    private sealed class ImpactSample { public double CpuMs { get; set; } public long DiskBytes { get; set; } public DateTime Time { get; set; } }

    private static string Classify(double cpuMs, long diskBytes) =>
        cpuMs > 1000 || diskBytes > 3 << 20 ? "high" : cpuMs >= 300 || diskBytes >= 300 << 10 ? "medium" : "low";

    private void ApplyImpact(List<StartupEntry> list)
    {
        // Preferred source: the trace Windows records at every logon (what Task Manager's "Startup impact" shows). Admin only.
        var windows = new Dictionary<string, (double Cpu, long Disk)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string dir = System.IO.Path.Combine(Paths.Windows, @"System32\wdi\LogFiles\StartupInfo");
            var latest = new DirectoryInfo(dir).EnumerateFiles("*.xml").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (latest is not null)
                foreach (var p in XDocument.Load(latest.FullName).Descendants("Process"))
                {
                    string? name = p.Attribute("Name")?.Value;
                    if (name is null) continue;
                    double cpu = double.TryParse(p.Element("CpuUsage")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c / 1000.0 : 0;
                    long disk = long.TryParse(p.Element("DiskUsage")?.Value, out var d) ? d : 0;
                    var cur = windows.GetValueOrDefault(name);
                    windows[name] = (cur.Cpu + cpu, cur.Disk + disk);
                }
        }
        catch { /* not elevated, or tracing disabled */ }

        var own = _store.Load(System.IO.Path.Combine("startup", "impact.json"), () => new Dictionary<string, ImpactSample>(StringComparer.OrdinalIgnoreCase));
        foreach (var e in list)
        {
            if (e.Path is null) continue;
            if (windows.TryGetValue(e.Path, out var w)) { e.CpuMs = w.Cpu; e.DiskBytes = w.Disk; e.Impact = Classify(w.Cpu, w.Disk); e.ImpactSource = "windows"; }
            else if (own.TryGetValue(e.Path, out var o)) { e.CpuMs = o.CpuMs; e.DiskBytes = o.DiskBytes; e.Impact = Classify(o.CpuMs, o.DiskBytes); e.ImpactSource = "opticore"; }
        }
    }

    /// <summary>Runs once, about two and a half minutes after boot: CPU time and I/O of each startup app so far.</summary>
    private void MeasureImpact()
    {
        var entries = List(true).Where(e => e.Enabled && e.Path is not null && e.Kind is not "service").ToList();
        var procs = _processes.Snapshot(0);
        var own = _store.Load(System.IO.Path.Combine("startup", "impact.json"), () => new Dictionary<string, ImpactSample>(StringComparer.OrdinalIgnoreCase));
        foreach (var group in procs.Where(p => p.Path is not null).GroupBy(p => p.Path!, StringComparer.OrdinalIgnoreCase))
        {
            if (!entries.Any(e => string.Equals(e.Path, group.Key, StringComparison.OrdinalIgnoreCase))) continue;
            double cpu = group.Sum(p => (double)p.CpuTimeMs);
            // Average with the previous boot so one unusual start does not decide the label.
            if (own.TryGetValue(group.Key, out var prev)) cpu = (cpu + prev.CpuMs) / 2;
            own[group.Key] = new ImpactSample { CpuMs = cpu, DiskBytes = prev?.DiskBytes ?? 0, Time = DateTime.UtcNow };
        }
        _store.Save(System.IO.Path.Combine("startup", "impact.json"), own);
        lock (_gate) _cache = null;
    }

    // ------------------------------------------------------------------ changes
    private StartupEntry Find(string id) => List().FirstOrDefault(e => e.Id == id) ?? throw RpcException.NotFound(Loc.S("Başlangıç öğesi bulunamadı.", "Startup item not found."));

    private void SetEnabled(ChangeScope scope, StartupEntry e, bool enabled)
    {
        switch (e.Kind)
        {
            case "registry" or "folder":
                var data = new byte[12];
                data[0] = (byte)(enabled ? 2 : 3);
                if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
                scope.RegSet(e.ApprovedKey!, e.ApprovedName!, RegistryValueKind.Binary, data);
                break;
            case "uwp":
                int before = Reg.GetInt(e.ApprovedKey!, "State") ?? 0;
                scope.Custom("startup.uwp", e.ApprovedKey!, new() { ["state"] = before.ToString() }, new() { ["state"] = enabled ? "2" : "1" }, () =>
                {
                    using var key = Reg.Create(e.ApprovedKey!);
                    key.SetValue("State", enabled ? 2 : 1, RegistryValueKind.DWord);
                });
                break;
            case "task":
                scope.Task(e.Id["task:".Length..], enabled);
                break;
            case "service":
                if (e.Microsoft) throw RpcException.Denied(Loc.S("Windows hizmetleri buradan değiştirilemez; güvenli olanlar Windows Ayarları sayfasındadır.", "Windows services cannot be changed here; the safe ones are on the Windows Tweaks page."));
                scope.Service(e.Location, enabled ? "auto" : "demand");
                break;
        }
    }

    private object Toggle(string[] ids, bool enabled)
    {
        var entries = ids.Select(Find).ToList();
        using var scope = _journal.Begin("startup", entries.Count == 1
            ? (enabled ? Loc.T("Başlangıçta etkinleştirildi: {0}", "Enabled at startup: {0}") : Loc.T("Başlangıçtan çıkarıldı: {0}", "Disabled at startup: {0}")).F(entries[0].Name)
            : (enabled ? Loc.T("{0} başlangıç öğesi etkinleştirildi", "{0} startup items enabled") : Loc.T("{0} başlangıç öğesi devre dışı bırakıldı", "{0} startup items disabled")).F(entries.Count));
        foreach (var e in entries) SetEnabled(scope, e, enabled);
        var set = scope.Commit();
        lock (_gate) _cache = null;
        if (scope.NeedsElevation && set.Applied == 0) throw RpcException.Elevation();
        return new { applied = set.Applied, failed = set.Failed, simulated = set.Simulated, needsElevation = scope.NeedsElevation };
    }

    // ------------------------------------------------------------------ startup guard
    private static readonly System.Text.RegularExpressions.Regex Volatile = new(
        @"\{[0-9A-Fa-f-]{36}\}|[_-]?[0-9A-Fa-f]{16,}|\d+(\.\d+)+|S-1-5-21[-\d]+|\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Identity of a startup item that survives updates: updaters re-register themselves with the version,
    /// a GUID or a hash in the name (GoogleUpdaterTaskSystem159.0.8{…}, MicrosoftEdgeAutoLaunch_3F2A…).
    /// </summary>
    internal static string StableId(string id) => Volatile.Replace(id, "").TrimEnd(' ', '_', '-').ToLowerInvariant();

    /// <summary>Publishers whose verified, signed updaters and helpers are not worth an alarm.</summary>
    private static readonly string[] TrustedPublishers =
        ["Microsoft", "Google LLC", "Mozilla Corporation", "Apple Inc.", "Intel Corporation", "Advanced Micro Devices", "NVIDIA Corporation", "Valve", "Adobe Inc.", "Realtek", "Logitech", "Epic Games", "Opera Norway", "Brave Software", "Spotify AB", "Discord Inc."];

    internal static bool IsTrustedUpdater(StartupEntry e)
    {
        if (e.Flags.Count > 0 || e.Path is null) return false;
        var sig = Signature.Check(e.Path);
        return sig.Signed && sig.Trusted && TrustedPublishers.Any(p => sig.Signer?.Contains(p, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void Guard()
    {
        var entries = List(true).Where(e => e.Kind != "service" || !e.Microsoft).ToList();
        var current = entries.Select(e => StableId(e.Id)).ToHashSet();
        var known = _store.Load<HashSet<string>?>(System.IO.Path.Combine("startup", "known.json"), () => null);
        _store.Save(System.IO.Path.Combine("startup", "known.json"), current);
        if (known is null || !_settings.Current.Alerts.StartupGuard) return; // first run only records the baseline
        known = known.Select(StableId).ToHashSet(); // baselines saved by older versions held raw ids
        foreach (var e in entries.Where(e => !known.Contains(StableId(e.Id)) && !IsTrustedUpdater(e)).GroupBy(e => StableId(e.Id)).Select(g => g.First()))
        {
            var (id, name) = (StableId(e.Id), e.Name);
            _notices.Add(e.Flags.Count > 0 || !e.Signed ? NoticeLevel.Warn : NoticeLevel.Info,
                Loc.T("Başlangıca yeni bir öğe eklendi: {0}", "A new startup item was added: {0}").F(name),
                Loc.T("Bu öğe artık Windows açılırken çalışacak. Tanımıyorsanız Başlangıç sayfasından devre dışı bırakabilirsiniz.",
                      "It will now run when Windows starts. If you do not recognize it, you can disable it on the Startup page."),
                "startup", "startup-new:" + id, coolDownHours: 24 * 30);
        }
    }

    // ------------------------------------------------------------------ boot history
    public static (List<BootRecord> Boots, bool Denied) BootHistory()
    {
        var boots = new List<BootRecord>();
        try
        {
            var query = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[(EventID=100)]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (EventRecord? ev = reader.ReadEvent(); ev is not null && boots.Count < 40; ev = reader.ReadEvent())
                using (ev)
                {
                    var data = XDocument.Parse(ev.ToXml()).Descendants().Where(x => x.Name.LocalName == "Data").ToDictionary(x => x.Attribute("Name")?.Value ?? "", x => x.Value);
                    long Get(string key) => data.TryGetValue(key, out var v) && long.TryParse(v, out var n) ? n : 0;
                    if (Get("BootTime") > 0)
                        boots.Add(new BootRecord { Time = ev.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow, TotalMs = Get("BootTime"), MainPathMs = Get("MainPathBootTime"), PostBootMs = Get("BootPostBootTime") });
                }
            boots.Reverse();
            return (boots, false);
        }
        catch (UnauthorizedAccessException) { return (boots, true); }
        catch (EventLogException) { return (boots, !Elevation.IsElevated); }
    }

    public IEnumerable<Finding> Findings()
    {
        var entries = List().Where(e => e.Kind != "service").ToList();
        var heavy = entries.Where(e => e.Enabled && e.Impact == "high").ToList();
        if (heavy.Count >= 2)
            yield return new Finding
            {
                Id = "startup.heavy", Group = "performance", Severity = heavy.Count >= 4 ? Severity.Warning : Severity.Info, Penalty = heavy.Count >= 4 ? 8 : 0,
                Title = Loc.T("{0} uygulama açılışı belirgin biçimde yavaşlatıyor", "{0} apps noticeably slow down startup").F(heavy.Count),
                Detail = Loc.T("Bu uygulamalar açılışta ölçülen yüksek işlemci süresi harcıyor. İhtiyacınız olmayanları başlangıçtan çıkarmak, uygulamayı kaldırmaz; yalnızca siz açana kadar bekletir.",
                               "These apps use a measured, large amount of processor time at startup. Removing the ones you do not need from startup does not uninstall them; they simply wait until you open them."),
                Impact = Loc.T("Açılışta yaklaşık {0:0.0} sn işlemci süresi serbest kalır.", "Frees about {0:0.0}s of processor time at startup.").F(heavy.Sum(e => e.CpuMs ?? 0) / 1000.0),
            }.Ev(Loc.T("Yüksek etkili", "High impact"), string.Join(", ", heavy.Take(5).Select(e => e.Name)))
             .Go(Loc.T("Başlangıcı yönet", "Manage startup"), "startup");

        var orphans = entries.Where(e => e.Enabled && e.FileMissing).ToList();
        if (orphans.Count > 0)
            yield return new Finding
            {
                Id = "startup.orphans", Group = "maintenance", Severity = Severity.Info,
                Title = Loc.T("{0} başlangıç girdisi var olmayan bir dosyayı gösteriyor", "{0} startup entries point to a file that no longer exists").F(orphans.Count),
                Detail = Loc.T("Kaldırılmış programlardan kalan girdiler. Zararsızdırlar ama her açılışta boşuna denenirler.", "Leftovers from uninstalled programs. Harmless, but they are tried in vain at every startup."),
            }.Ev(Loc.T("Girdiler", "Entries"), string.Join(", ", orphans.Take(5).Select(e => e.Name)))
             .Fix(Loc.T("Devre dışı bırak", "Disable them"), "startup.toggle", new { ids = orphans.Select(e => e.Id).ToArray(), enabled = false }, safe: true)
             .Go(Loc.T("Başlangıcı aç", "Open startup"), "startup");
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("startup.list", c =>
        {
            var items = List(c.Bool("refresh"));
            return new
            {
                items,
                measured = items.Any(i => i.ImpactSource is not null),
                enabled = items.Count(i => i.Enabled && i.Kind != "service"),
            };
        });
        rpc.On("startup.toggle", c => Toggle(c.StrArr("ids"), c.Bool("enabled")));
        rpc.On("startup.serviceMode", c =>
        {
            var e = Find(c.Str("id"));
            string mode = c.Str("mode");
            if (e.Kind != "service") throw RpcException.Invalid("Not a service");
            if (e.Microsoft) throw RpcException.Denied(Loc.S("Windows hizmetleri buradan değiştirilemez.", "Windows services cannot be changed here."));
            using var scope = _journal.Begin("startup", Loc.T("Hizmet başlatma türü: {0} → {1}", "Service start type: {0} → {1}").F(e.Name, mode));
            scope.Service(e.Location, mode);
            var set = scope.Commit();
            lock (_gate) _cache = null;
            if (scope.NeedsElevation) throw RpcException.Elevation();
            return new { applied = set.Applied, simulated = set.Simulated };
        });
        rpc.On("startup.boots", _ =>
        {
            var (boots, denied) = BootHistory();
            return new { boots, denied };
        });
    }
}
