using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public sealed class InstalledApp
{
    public string Id { get; set; } = "";
    /// <summary>win32 | store</summary>
    public string Kind { get; set; } = "win32";
    public string Name { get; set; } = "";
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public DateTime? Installed { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>registry (as reported by the installer) | measured (folder size) | null</summary>
    public string? SizeSource { get; set; }
    public string? Location { get; set; }
    public string? Icon { get; set; }
    public string? Uninstall { get; set; }
    public string? QuietUninstall { get; set; }
    public bool Machine { get; set; }
    public bool SystemComponent { get; set; }
    public bool Bloat { get; set; }
    public LText? BloatReason { get; set; }
    public bool Removable { get; set; } = true;
    public DateTime? LastUsed { get; set; }
    public string? PackageFullName { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public string? RegKey { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public RegistryView View { get; set; }
}

public sealed record Leftover(string Kind, string Path, long Bytes, LText Reason);

/// <summary>Uninstaller: every Win32 and Store app, sizes, bloat detection, silent removal and leftover sweeping.</summary>
public sealed class AppsService
{
    private readonly ChangeJournal _journal;
    private readonly JobManager _jobs;
    private readonly object _gate = new();
    private List<InstalledApp>? _cache;
    private long _cacheAt;
    private readonly Dictionary<string, (string Name, string? Publisher, string? Location, DateTime At)> _removed = [];

    // Preinstalled consumer apps that most people never open. Matched against Store package names.
    private static readonly (string Pattern, LText Why)[] StoreBloat =
    [
        ("Clipchamp.Clipchamp", Loc.T("Önceden yüklü video düzenleyici", "Preinstalled video editor")),
        ("Microsoft.BingNews", Loc.T("Haber uygulaması", "News app")), ("Microsoft.BingWeather", Loc.T("Hava durumu uygulaması", "Weather app")),
        ("Microsoft.BingSearch", Loc.T("Bing arama uygulaması", "Bing search app")), ("Microsoft.GetHelp", Loc.T("Yardım Al uygulaması", "Get Help app")),
        ("Microsoft.Getstarted", Loc.T("İpuçları uygulaması", "Tips app")), ("Microsoft.MicrosoftSolitaireCollection", Loc.T("Reklam içeren oyun", "Game with ads")),
        ("Microsoft.MicrosoftOfficeHub", Loc.T("Office tanıtım uygulaması", "Office promotion app")), ("Microsoft.People", Loc.T("Kişiler uygulaması", "People app")),
        ("Microsoft.PowerAutomateDesktop", Loc.T("Otomasyon aracı (çoğu kişi kullanmaz)", "Automation tool (rarely used)")), ("Microsoft.Todos", Loc.T("Yapılacaklar uygulaması", "To Do app")),
        ("Microsoft.WindowsFeedbackHub", Loc.T("Geri bildirim uygulaması", "Feedback app")), ("Microsoft.WindowsMaps", Loc.T("Haritalar", "Maps")),
        ("Microsoft.ZuneMusic", Loc.T("Medya Oynatıcı (eski Groove)", "Media Player (old Groove)")), ("Microsoft.YourPhone", Loc.T("Telefon Bağlantısı", "Phone Link")),
        ("Microsoft.MicrosoftStickyNotes", Loc.T("Yapışkan Notlar", "Sticky Notes")), ("MicrosoftCorporationII.QuickAssist", Loc.T("Uzaktan Yardım", "Quick Assist")),
        ("Microsoft.Windows.DevHome", Loc.T("Geliştirici panosu", "Developer dashboard")), ("MicrosoftTeams", Loc.T("Kişisel Teams", "Teams (personal)")),
        ("Microsoft.OutlookForWindows", Loc.T("Yeni Outlook", "New Outlook")), ("Microsoft.549981C3F5F10", "Cortana"),
        ("SpotifyAB.SpotifyMusic", Loc.T("Önceden yüklü Spotify", "Preinstalled Spotify")), ("Disney.", Loc.T("Önceden yüklü uygulama", "Preinstalled app")),
        ("king.com.", Loc.T("Önceden yüklü oyun", "Preinstalled game")), ("Facebook.", Loc.T("Önceden yüklü uygulama", "Preinstalled app")),
        ("AmazonVideo.", Loc.T("Önceden yüklü uygulama", "Preinstalled app")), ("BytedancePte.", Loc.T("Önceden yüklü uygulama", "Preinstalled app")),
        ("5A894077.McAfeeSecurity", Loc.T("Deneme sürümü güvenlik yazılımı", "Trial security software")),
    ];

    // Store packages Windows needs: never offered for removal.
    private static readonly string[] StoreKeep =
    [
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.Windows.Photos", "Microsoft.WindowsCalculator",
        "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.WindowsTerminal", "Microsoft.SecHealthUI", "Microsoft.VCLibs",
        "Microsoft.UI.Xaml", "Microsoft.NET.", "Microsoft.WindowsAppRuntime", "Microsoft.HEIFImageExtension", "Microsoft.HEVCVideoExtension", "Microsoft.VP9VideoExtensions",
        "Microsoft.WebMediaExtensions", "Microsoft.WebpImageExtension", "Microsoft.RawImageExtension", "Microsoft.AV1VideoExtension", "Microsoft.MPEG2VideoExtension",
        "Microsoft.Services.Store", "Microsoft.WindowsCamera", "Microsoft.ApplicationCompatibilityEnhancements", "MicrosoftWindows.Client", "Microsoft.Winget",
        "Microsoft.XboxIdentityProvider", "Microsoft.XboxGameCallableUI", "Microsoft.GamingServices", "Microsoft.WidgetsPlatformRuntime", "Microsoft.Edge",
    ];

    // Trial and OEM utilities that ship on new PCs. Matched against display names.
    private static readonly (string Pattern, LText Why)[] Win32Bloat =
    [
        ("McAfee", Loc.T("Deneme sürümü güvenlik yazılımı: Windows Defender zaten koruyor ve ikisi birlikte sistemi yavaşlatır", "Trial security software: Windows Defender already protects you and running both slows the PC")),
        ("Norton", Loc.T("Deneme sürümü güvenlik yazılımı", "Trial security software")), ("Avast", Loc.T("Reklam gösteren ücretsiz güvenlik yazılımı", "Free security software that shows ads")),
        ("WildTangent", Loc.T("Önceden yüklü oyun mağazası", "Preinstalled game store")), ("ExpressVPN", Loc.T("Önceden yüklü deneme", "Preinstalled trial")),
        ("Dropbox Promotion", Loc.T("Tanıtım uygulaması", "Promotion app")), ("Booking.com", Loc.T("Kısayol olarak yüklenmiş tanıtım", "Promotional shortcut")),
        ("Acer Jumpstart", Loc.T("Üretici tanıtım aracı", "Manufacturer promo tool")), ("Acer Collection", Loc.T("Üretici uygulama mağazası", "Manufacturer app store")),
        ("HP Wolf Security", Loc.T("Üretici güvenlik eklentisi, belirgin kaynak tüketir", "Manufacturer security add-on with notable overhead")),
        ("Lenovo Now", Loc.T("Üretici tanıtım aracı", "Manufacturer promo tool")), ("ASUS GiftBox", Loc.T("Üretici tanıtım aracı", "Manufacturer promo tool")),
        ("CCleaner", Loc.T("Paket halinde gelen ek yazılım ve gizlilik sorunlarıyla bilinir", "Known for bundled extras and privacy issues")),
        ("Driver Booster", Loc.T("Sürücü güncelleyiciler yanlış sürücü kurabilir; sürücüleri üreticiden alın", "Driver updaters can install the wrong driver; get drivers from the manufacturer")),
        ("Driver Easy", Loc.T("Sürücü güncelleyiciler yanlış sürücü kurabilir", "Driver updaters can install the wrong driver")),
        ("Advanced SystemCare", Loc.T("Ölçülebilir faydası olmayan, sürekli çalışan optimizasyon aracı", "Always-running optimizer with no measurable benefit")),
        ("Web Companion", Loc.T("Tarayıcı ayarlarını değiştiren yardımcı", "Helper that changes browser settings")), ("Ask Toolbar", Loc.T("Tarayıcı araç çubuğu", "Browser toolbar")),
    ];

    public AppsService(ChangeJournal journal, JobManager jobs)
    {
        _journal = journal; _jobs = jobs;
    }

    // ------------------------------------------------------------------ enumeration
    public List<InstalledApp> List(bool refresh = false)
    {
        lock (_gate)
        {
            if (!refresh && _cache is not null && Environment.TickCount64 - _cacheAt < 60000) return _cache;
            var list = new List<InstalledApp>();
            Read(list, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Registry64, true);
            Read(list, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Registry32, true);
            Read(list, @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Registry64, false);
            try { ReadStore(list); } catch (Exception e) { Log.Warn("apps: store list", e); }
            // Duplicates: the same product often registers under both views.
            list = [.. list.GroupBy(a => (a.Name.ToLowerInvariant(), a.Version)).Select(g => g.OrderByDescending(a => a.SizeBytes).First())];
            _cache = [.. list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
            _cacheAt = Environment.TickCount64;
            return _cache;
        }
    }

    private static void Read(List<InstalledApp> list, string root, RegistryView view, bool machine)
    {
        foreach (var sub in Reg.SubKeys(root, view))
        {
            string key = root + "\\" + sub;
            string? name = Reg.GetStr(key, "DisplayName", view);
            if (string.IsNullOrWhiteSpace(name)) continue;
            bool system = Reg.GetInt(key, "SystemComponent", view) == 1;
            if (Reg.GetStr(key, "ParentKeyName", view) is { Length: > 0 } || Reg.GetStr(key, "ReleaseType", view) is "Update" or "Hotfix" or "Security Update") continue;
            string? uninstall = Reg.GetStr(key, "UninstallString", view);
            if (system && uninstall is null) continue;
            if (uninstall is null && Reg.GetStr(key, "QuietUninstallString", view) is null) continue;

            var app = new InstalledApp
            {
                Id = $"w:{(machine ? "m" : "u")}:{(view == RegistryView.Registry32 ? "32" : "64")}:{sub}", Name = name.Trim(),
                Publisher = Reg.GetStr(key, "Publisher", view)?.Trim(), Version = Reg.GetStr(key, "DisplayVersion", view),
                Location = Reg.GetStr(key, "InstallLocation", view)?.Trim('"', ' ', '\\') is { Length: > 3 } loc ? loc : null,
                Uninstall = uninstall, QuietUninstall = Reg.GetStr(key, "QuietUninstallString", view),
                Machine = machine, SystemComponent = system, RegKey = key, View = view,
            };
            string? icon = Reg.GetStr(key, "DisplayIcon", view);
            if (icon is not null) app.Icon = icon.Split(',')[0].Trim('"');
            if (Reg.GetInt(key, "EstimatedSize", view) is int kb && kb > 0) { app.SizeBytes = kb * 1024L; app.SizeSource = "registry"; }
            if (DateTime.TryParseExact(Reg.GetStr(key, "InstallDate", view), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)) app.Installed = d.ToUniversalTime();
            var bloat = Win32Bloat.FirstOrDefault(b => app.Name.Contains(b.Pattern, StringComparison.OrdinalIgnoreCase));
            if (bloat.Pattern is not null) { app.Bloat = true; app.BloatReason = bloat.Why; }
            if (app.Location is not null && Directory.Exists(app.Location) && app.Icon is null)
                app.Icon = Directory.EnumerateFiles(app.Location, "*.exe", FileCleaner.Flat).FirstOrDefault();
            list.Add(app);
        }
    }

    private static void ReadStore(List<InstalledApp> list)
    {
        // Get-AppxPackage is the only supported enumeration without WinRT projections; one PowerShell call for all packages.
        var r = Proc.PowerShell("Get-AppxPackage | Where-Object { -not $_.IsFramework -and $_.SignatureKind -ne 'System' } | ForEach-Object { " +
            "$m = $null; try { $m = Get-AppxPackageManifest $_ } catch {}; $d = if ($m) { $m.Package.Properties.DisplayName } else { $_.Name }; " +
            "[pscustomobject]@{ n=$_.Name; f=$_.PackageFullName; fam=$_.PackageFamilyName; v=$_.Version; p=$_.Publisher; l=$_.InstallLocation; d=$d; nr=$_.NonRemovable } } | ConvertTo-Json -Compress", 90000);
        if (!r.Ok || r.StdOut.Trim().Length == 0) return;
        using var doc = System.Text.Json.JsonDocument.Parse(r.StdOut.Trim());
        var items = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        foreach (var e in items)
        {
            string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
            string name = S("n"), full = S("f"), display = S("d");
            if (display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) display = ShellOps.PackageString(full, display) ?? ShellOps.PrettyPackageName(name);
            if (display.Length == 0) display = ShellOps.PrettyPackageName(name);
            bool keep = StoreKeep.Any(k => name.StartsWith(k, StringComparison.OrdinalIgnoreCase)) || (e.TryGetProperty("nr", out var nr) && nr.ValueKind == System.Text.Json.JsonValueKind.True);
            var bloat = StoreBloat.FirstOrDefault(b => name.StartsWith(b.Pattern, StringComparison.OrdinalIgnoreCase));
            string publisher = S("p");
            int cn = publisher.IndexOf("CN=", StringComparison.Ordinal);
            var app = new InstalledApp
            {
                Id = "s:" + full, Kind = "store", Name = display, Version = S("v"), Location = S("l"), PackageFullName = full,
                Publisher = cn >= 0 ? publisher[(cn + 3)..].Split(',')[0] : publisher, Removable = !keep,
                Bloat = !keep && bloat.Pattern is not null, BloatReason = bloat.Pattern is null ? (LText?)null : bloat.Why,
            };
            try { if (Directory.Exists(app.Location)) app.Installed = Directory.GetCreationTimeUtc(app.Location); } catch { /* no access */ }
            list.Add(app);
        }
    }

    /// <summary>Measures folder sizes for apps whose installer reported none.</summary>
    private object MeasureSizes(JobContext job)
    {
        var apps = List().Where(a => a.SizeSource is null && a.Location is not null && Directory.Exists(a.Location)).ToList();
        for (int i = 0; i < apps.Count; i++)
        {
            job.ThrowIfCancelled();
            job.Report(i / (double)apps.Count, Loc.T("Ölçülüyor: {0}", "Measuring: {0}").F(apps[i].Name));
            try
            {
                long size = 0;
                foreach (var f in new DirectoryInfo(apps[i].Location!).EnumerateFiles("*", FileCleaner.Walk)) size += f.Length;
                apps[i].SizeBytes = size;
                apps[i].SizeSource = "measured";
            }
            catch { /* unreadable */ }
        }
        return new { measured = apps.Count };
    }

    // ------------------------------------------------------------------ uninstall
    private static (string File, string Args) Split(string command)
    {
        command = Paths.Expand(command.Trim());
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return (command[1..end], command[(end + 1)..].Trim());
        }
        string? exe = Paths.ExeFromCommand(command);
        return exe is null ? (command, "") : (exe, command[exe.Length..].Trim());
    }

    /// <summary>Silent switches for the common installer frameworks, used when the app did not register a quiet uninstall command.</summary>
    private static string? QuietCommand(InstalledApp app)
    {
        if (app.QuietUninstall is { Length: > 0 }) return app.QuietUninstall;
        string u = app.Uninstall ?? "";
        if (u.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            int brace = u.IndexOf('{');
            return brace >= 0 ? $"MsiExec.exe /X{u[brace..(u.IndexOf('}', brace) + 1)]} /qn /norestart" : null;
        }
        if (u.Contains("unins0", StringComparison.OrdinalIgnoreCase)) return u + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART";     // Inno Setup
        if (u.Contains("Update.exe", StringComparison.OrdinalIgnoreCase) && u.Contains("--uninstall")) return u + " -s";             // Squirrel
        return null;
    }

    private object Uninstall(string[] ids, bool silent, JobContext job)
    {
        var apps = ids.Select(id => List().FirstOrDefault(a => a.Id == id) ?? throw RpcException.NotFound("App not found")).ToList();
        if (apps.Any(a => !a.Removable)) throw RpcException.Denied(Loc.S("Windows'un ihtiyaç duyduğu uygulamalar kaldırılamaz.", "Apps Windows needs cannot be removed."));
        var results = new List<object>();
        using var scope = _journal.Begin("apps", apps.Count == 1 ? Loc.T("Kaldırıldı: {0}", "Uninstalled: {0}").F(apps[0].Name) : Loc.T("{0} uygulama kaldırıldı", "{0} apps uninstalled").F(apps.Count));
        for (int i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            job.ThrowIfCancelled();
            job.Report(i / (double)apps.Count, Loc.T("Kaldırılıyor: {0}", "Uninstalling: {0}").F(app.Name));
            string status;
            string? error = null;
            if (scope.DryRun) status = "simulated";
            else if (app.Kind == "store")
            {
                var r = Proc.PowerShell($"Remove-AppxPackage -Package '{app.PackageFullName!.Replace("'", "''")}'", 180000);
                status = r.Ok ? "removed" : "failed";
                if (!r.Ok) error = r.Text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
            }
            else
            {
                string? quiet = silent ? QuietCommand(app) : null;
                string command = quiet ?? app.Uninstall ?? throw RpcException.Failed("No uninstall command");
                var (file, args) = Split(command);
                if (file.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("MsiExec.exe", StringComparison.OrdinalIgnoreCase)) file = Proc.Sys("msiexec.exe");
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, Verb = app.Machine && !Elevation.IsElevated ? "runas" : "" })!;
                    // The vendor's own uninstaller may show its UI; wait for it (up to 30 minutes).
                    p.WaitForExit(30 * 60 * 1000);
                    lock (_gate) _cache = null;
                    bool gone = !Reg.Exists(app.RegKey!, app.View);
                    status = gone ? "removed" : p.HasExited && p.ExitCode != 0 ? "failed" : "unknown";
                    if (status == "failed") error = "exit code " + p.ExitCode;
                }
                catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { status = "cancelled"; }
                catch (Exception e) { status = "failed"; error = e.Message; }
            }
            if (status is "removed" or "unknown") lock (_gate) _removed[app.Id] = (app.Name, app.Publisher, app.Location, DateTime.UtcNow);
            scope.Note("app.uninstall", app.Name, new() { ["version"] = app.Version, ["status"] = status, ["kind"] = app.Kind }, applied: status == "removed");
            results.Add(new { id = app.Id, name = app.Name, status, error, leftoversAvailable = status is "removed" or "unknown" });
        }
        lock (_gate) _cache = null;
        return new { results, simulated = scope.DryRun };
    }

    // ------------------------------------------------------------------ leftovers
    private static string[] Tokens(string? name, string? publisher)
    {
        // Distinctive words only: "Microsoft", "Inc", version numbers and short words would match unrelated things.
        string[] stop = ["microsoft", "corporation", "inc", "ltd", "llc", "gmbh", "software", "technologies", "the", "for", "and", "x64", "x86", "version", "edition", "update", "windows"];
        return [.. $"{name} {publisher}".Split([' ', '-', '_', '.', ',', '(', ')', '®', '™'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && !stop.Contains(w.ToLowerInvariant()) && !w.Any(char.IsDigit)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private object FindLeftovers(string? appId, string? name, string? publisher, string? location)
    {
        if (appId is not null) lock (_gate) if (_removed.TryGetValue(appId, out var r)) (name, publisher, location) = (r.Name, r.Publisher, r.Location);
        if (string.IsNullOrWhiteSpace(name)) throw RpcException.Invalid("Missing app name");
        var tokens = Tokens(name, null);
        var pubTokens = Tokens(publisher, null);
        if (tokens.Length == 0) return new { items = Array.Empty<object>() };
        var found = new List<Leftover>();
        bool installed(string path) => List(true).Any(a => a.Location is not null && path.StartsWith(a.Location, StringComparison.OrdinalIgnoreCase));

        if (location is not null && Directory.Exists(location) && FileCleaner.IsSafeRoot(location))
            found.Add(new Leftover("folder", location, DirSize(location), Loc.T("Uygulamanın kurulum klasörü hâlâ duruyor", "The app's install folder is still there")));

        foreach (var root in new[] { Paths.ProgramFiles, Paths.ProgramFilesX86, Paths.ProgramData, Paths.Roaming, Paths.Local, Paths.LocalLow })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in SafeDirs(root))
            {
                string leaf = Path.GetFileName(dir);
                bool match = tokens.All(tk => leaf.Contains(tk, StringComparison.OrdinalIgnoreCase)) && leaf.Length <= name.Length + 12;
                // Publisher folder containing the product folder, e.g. %AppData%\Vendor\Product
                if (!match && pubTokens.Any(p => leaf.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    foreach (var inner in SafeDirs(dir))
                        if (tokens.All(tk => Path.GetFileName(inner).Contains(tk, StringComparison.OrdinalIgnoreCase)) && !installed(inner) && !found.Any(f => f.Path == inner))
                            found.Add(new Leftover("folder", inner, DirSize(inner), Loc.T("Ada göre eşleşen klasör", "Folder matching the name")));
                if (match && !installed(dir) && FileCleaner.IsSafeRoot(dir) && !found.Any(f => f.Path == dir))
                    found.Add(new Leftover("folder", dir, DirSize(dir), Loc.T("Ada göre eşleşen klasör", "Folder matching the name")));
            }
        }
        foreach (var hive in new[] { @"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node" })
            foreach (var sub in Reg.SubKeys(hive))
            {
                bool match = tokens.All(tk => sub.Contains(tk, StringComparison.OrdinalIgnoreCase)) && sub.Length <= name.Length + 12;
                if (match) found.Add(new Leftover("registry", hive + "\\" + sub, 0, Loc.T("Ada göre eşleşen kayıt defteri anahtarı", "Registry key matching the name")));
                else if (pubTokens.Length > 0 && pubTokens.Any(p => sub.Equals(p, StringComparison.OrdinalIgnoreCase)))
                    foreach (var inner in Reg.SubKeys(hive + "\\" + sub).Where(i => tokens.All(tk => i.Contains(tk, StringComparison.OrdinalIgnoreCase))))
                        found.Add(new Leftover("registry", $@"{hive}\{sub}\{inner}", 0, Loc.T("Yayıncı anahtarı altında eşleşen ürün", "Matching product under the publisher's key")));
            }
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", FileCleaner.Walk).Where(l => tokens.All(tk => Path.GetFileName(l).Contains(tk, StringComparison.OrdinalIgnoreCase))))
                found.Add(new Leftover("shortcut", lnk, new FileInfo(lnk).Length, Loc.T("Başlat menüsünde kalan kısayol", "Shortcut left in the Start menu")));
        }
        return new { items = found.Take(60).ToList(), name };
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root, "*", FileCleaner.Flat).ToList(); } catch { return []; }
    }

    private static long DirSize(string dir)
    {
        long size = 0;
        try { foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", FileCleaner.Walk)) size += f.Length; } catch { /* partial */ }
        return size;
    }

    private object RemoveLeftovers(string[] folders, string[] keys, string[] files)
    {
        int ok = 0, failed = 0;
        using var scope = _journal.Begin("apps", Loc.T("Kaldırma artıkları temizlendi ({0} öğe)", "Uninstall leftovers removed ({0} items)").F(folders.Length + keys.Length + files.Length));
        foreach (var key in keys)
        {
            if (key.Count(c => c == '\\') < 2) { failed++; continue; } // never a hive or a top-level key
            var e = scope.RegDeleteKey(key);
            if (e.Error is null) ok++; else failed++;
        }
        foreach (var path in folders.Concat(files))
        {
            if (!FileCleaner.IsSafeRoot(path)) { failed++; continue; }
            scope.Note("files.recycled", path);
            if (scope.DryRun || ShellOps.Recycle(path)) ok++; else failed++;
        }
        return new { done = ok, failed, simulated = scope.DryRun };
    }

    public IEnumerable<Finding> Findings()
    {
        var bloat = List().Where(a => a.Bloat).ToList();
        if (bloat.Count >= 3)
            yield return new Finding
            {
                Id = "apps.bloat", Group = "maintenance", Severity = Severity.Info,
                Title = Loc.T("{0} önceden yüklü ya da gereksiz uygulama var", "{0} preinstalled or unnecessary apps are installed").F(bloat.Count),
                Detail = Loc.T("Bilgisayarla birlikte gelen deneme sürümleri ve tanıtım uygulamaları. Kullanmıyorsanız kaldırmak yer açar ve arka plan yükünü azaltır.", "Trials and promotional apps that came with the PC. Removing the ones you do not use frees space and reduces background load."),
            }.Ev(Loc.T("Uygulamalar", "Apps"), string.Join(", ", bloat.Take(5).Select(b => b.Name))).Go(Loc.T("İncele", "Review"), "apps/bloat");
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("apps.list", c =>
        {
            var items = List(c.Bool("refresh"));
            return new { items, total = items.Sum(a => a.SizeBytes), store = items.Count(a => a.Kind == "store"), bloat = items.Count(a => a.Bloat) };
        });
        rpc.On("apps.measure", _ => _jobs.Start("apps.measure", Loc.T("Uygulama boyutları ölçülüyor", "Measuring app sizes"), MeasureSizes));
        rpc.On("apps.uninstall", c =>
        {
            var ids = c.StrArr("ids");
            bool silent = c.Bool("silent");
            return _jobs.Start("apps.uninstall", Loc.T("Uygulama kaldırma", "Uninstalling apps"), job => Uninstall(ids, silent, job));
        });
        rpc.On("apps.leftovers", c => FindLeftovers(c.StrOrNull("id"), c.StrOrNull("name"), c.StrOrNull("publisher"), c.StrOrNull("location")));
        rpc.On("apps.removeLeftovers", c => RemoveLeftovers(c.StrArr("folders"), c.StrArr("keys"), c.StrArr("files")));
        rpc.On("apps.portable", _ => Portable());
    }

    /// <summary>Executables in user folders that are not registered as installed: portable apps.</summary>
    private object Portable()
    {
        var known = List().Where(a => a.Location is not null).Select(a => a.Location!).ToList();
        var result = new List<object>();
        foreach (var root in new[] { Path.Combine(Paths.Profile, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Path.Combine(Paths.Profile, "Tools"), Path.Combine(Paths.Profile, "Apps"), Path.Combine(Paths.Profile, "PortableApps") }.Where(Directory.Exists))
        {
            IEnumerable<string> exes;
            try { exes = Directory.EnumerateFiles(root, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }).Take(400).ToList(); } catch { continue; }
            foreach (var exe in exes)
            {
                if (known.Any(k => exe.StartsWith(k, StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    var v = FileVersionInfo.GetVersionInfo(exe);
                    string n = Path.GetFileName(exe).ToLowerInvariant();
                    bool installer = n.Contains("setup") || n.Contains("install") || (v.FileDescription ?? "").Contains("Setup", StringComparison.OrdinalIgnoreCase) || (v.FileDescription ?? "").Contains("Install", StringComparison.OrdinalIgnoreCase);
                    var fi = new FileInfo(exe);
                    result.Add(new { path = exe, name = string.IsNullOrWhiteSpace(v.FileDescription) ? Path.GetFileNameWithoutExtension(exe) : v.FileDescription, company = v.CompanyName, version = v.FileVersion, bytes = fi.Length, modified = fi.LastWriteTimeUtc, installer });
                }
                catch { /* unreadable */ }
            }
        }
        return result;
    }
}
