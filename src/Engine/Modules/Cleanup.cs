using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

/// <summary>A folder (or file pattern in it) that a cleanup category may empty.</summary>
public sealed record CleanTarget(string Path, string Pattern = "*", bool Recursive = true, int MinAgeDays = 0, string? Label = null);

public sealed class CleanCategory
{
    public string Id { get; init; } = "";
    /// <summary>system | browsers | apps | dev</summary>
    public string Group { get; init; } = "system";
    public LText Title { get; init; }
    public LText Description { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool DefaultOn { get; init; }
    public LText? Note { get; init; }
    public Func<List<CleanTarget>> Targets { get; init; } = () => [];
    /// <summary>Categories that are not plain folders (the Recycle Bin).</summary>
    public Func<(long Bytes, long Items)>? CustomScan { get; init; }
    public Action? CustomClean { get; init; }
}

public sealed class CategoryResult
{
    public string Id { get; set; } = "";
    public string Group { get; set; } = "";
    public LText Title { get; set; }
    public LText Description { get; set; }
    public LText? Note { get; set; }
    public bool RequiresAdmin { get; set; }
    public bool DefaultOn { get; set; }
    /// <summary>False when the category needs administrator rights the app does not have.</summary>
    public bool Available { get; set; } = true;
    public long Bytes { get; set; }
    public long Files { get; set; }
    public List<FileEntry> Largest { get; set; } = [];
    public List<SubTotal> Parts { get; set; } = [];
}

public sealed record FileEntry(string Path, long Bytes, DateTime Modified);
public sealed record SubTotal(string Label, long Bytes, long Files);

public sealed class CleanScan
{
    public DateTime Time { get; set; }
    public List<CategoryResult> Categories { get; set; } = [];
    public long TotalBytes { get; set; }
    /// <summary>What the default selection would free.</summary>
    public long RecommendedBytes { get; set; }
}

public sealed class CleanRun
{
    public DateTime Time { get; set; }
    public long FreedBytes { get; set; }
    public long Deleted { get; set; }
    public long Skipped { get; set; }
    public bool Simulated { get; set; }
    public List<string> Categories { get; set; } = [];
}

/// <summary>Low-level, guarded file deletion shared by every cleanup feature.</summary>
internal static class FileCleaner
{
    public static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint, // never follow junctions or symlinks out of the target
    };

    public static readonly EnumerationOptions Flat = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = true, ReturnSpecialDirectories = false, AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly Lazy<HashSet<string>> Forbidden = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p) { if (!string.IsNullOrEmpty(p)) set.Add(Path.GetFullPath(p).TrimEnd('\\')); }
        Add(Paths.Windows); Add(Environment.SystemDirectory); Add(Paths.ProgramFiles); Add(Paths.ProgramFilesX86); Add(Paths.ProgramData);
        Add(Paths.Profile); Add(Paths.Local); Add(Paths.Roaming); Add(Paths.LocalLow); Add(Path.Combine(Paths.Profile, "AppData"));
        foreach (var f in new[] { Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos })
            Add(Environment.GetFolderPath(f));
        Add(Path.Combine(Paths.Profile, "Downloads"));
        Add(Path.GetDirectoryName(Paths.Profile));
        return set;
    });

    /// <summary>A cleanup root must be a real sub-folder: never a drive root, the profile, Windows or Program Files themselves.</summary>
    public static bool IsSafeRoot(string path)
    {
        try
        {
            string full = Path.GetFullPath(path).TrimEnd('\\');
            string root = (Path.GetPathRoot(full) ?? "").TrimEnd('\\');
            if (full.Length <= root.Length + 1) return false;
            return !Forbidden.Value.Contains(full);
        }
        catch { return false; }
    }

    public static IEnumerable<FileInfo> Enumerate(CleanTarget t)
    {
        if (!Directory.Exists(t.Path) || !IsSafeRoot(t.Path)) yield break;
        DateTime cutoff = t.MinAgeDays > 0 ? DateTime.UtcNow.AddDays(-t.MinAgeDays) : DateTime.MaxValue;
        IEnumerable<FileInfo> files;
        try { files = new DirectoryInfo(t.Path).EnumerateFiles(t.Pattern, t.Recursive ? Walk : Flat); }
        catch { yield break; }
        using var e = files.GetEnumerator();
        while (true)
        {
            FileInfo? f;
            try { if (!e.MoveNext()) break; f = e.Current; }
            catch { break; }
            DateTime modified;
            try { modified = f.LastWriteTimeUtc; } catch { continue; }
            if (modified <= cutoff) yield return f;
        }
    }

    /// <summary>Deletes what the target matches. Files in use are skipped, never forced.</summary>
    public static (long Bytes, long Files, long Skipped) Delete(CleanTarget t, bool dryRun, CancellationToken ct)
    {
        long bytes = 0, files = 0, skipped = 0;
        foreach (var f in Enumerate(t))
        {
            ct.ThrowIfCancellationRequested();
            long size;
            try { size = f.Length; } catch { continue; }
            if (dryRun) { bytes += size; files++; continue; }
            try
            {
                if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes = FileAttributes.Normal;
                f.Delete();
                bytes += size;
                files++;
            }
            catch { skipped++; }
        }
        if (!dryRun && t.Recursive && Directory.Exists(t.Path)) RemoveEmptyDirs(t.Path, ct);
        return (bytes, files, skipped);
    }

    private static void RemoveEmptyDirs(string root, CancellationToken ct)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", Walk).OrderByDescending(d => d.Length))
            {
                ct.ThrowIfCancellationRequested();
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir, false); }
                catch { /* in use or not empty */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best effort */ }
    }
}

public sealed class CleanupService
{
    private readonly ChangeJournal _journal;
    private readonly JobManager _jobs;
    private readonly DataStore _store;
    private readonly List<CleanCategory> _catalog;
    private CleanScan? _last;

    public CleanScan? LastScan => _last;

    public CleanupService(ChangeJournal journal, JobManager jobs, DataStore store)
    {
        _journal = journal; _jobs = jobs; _store = store;
        _catalog = BuildCatalog();
        _last = store.Load<CleanScan?>(Path.Combine("cleanup", "last-scan.json"), () => null);
    }

    // ------------------------------------------------------------------ catalog
    private static List<CleanTarget> Existing(params CleanTarget[] targets) => [.. targets.Where(t => Directory.Exists(t.Path))];

    private static IEnumerable<string> ChromiumProfiles(string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(userData))
        {
            string name = Path.GetFileName(dir);
            if (name == "Default" || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) yield return dir;
        }
    }

    private static List<CleanTarget> Chromium(string userData, string label)
    {
        var list = new List<CleanTarget>();
        foreach (var profile in ChromiumProfiles(userData))
            foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache", "DawnCache", "DawnWebGPUCache" })
            {
                var p = Path.Combine(profile, sub);
                if (Directory.Exists(p)) list.Add(new CleanTarget(p, Label: label));
            }
        foreach (var sub in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache", @"Crashpad\reports" })
        {
            var p = Path.Combine(userData, sub);
            if (Directory.Exists(p)) list.Add(new CleanTarget(p, Label: label));
        }
        return list;
    }

    /// <summary>Electron apps (Discord, Slack, VS Code, Teams...) all keep the same three cache folders in %APPDATA%\&lt;app&gt;.</summary>
    private static List<CleanTarget> ElectronCaches()
    {
        var list = new List<CleanTarget>();
        if (!Directory.Exists(Paths.Roaming)) return list;
        foreach (var app in Directory.EnumerateDirectories(Paths.Roaming))
        {
            try
            {
                if (!Directory.Exists(Path.Combine(app, "Code Cache")) || !Directory.Exists(Path.Combine(app, "GPUCache"))) continue;
                string label = Path.GetFileName(app);
                foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnWebGPUCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache", "CachedData", "Crashpad" })
                {
                    var p = Path.Combine(app, sub);
                    if (Directory.Exists(p)) list.Add(new CleanTarget(p, Label: label));
                }
            }
            catch { /* unreadable app folder */ }
        }
        return list;
    }

    private static List<CleanCategory> BuildCatalog()
    {
        string local = Paths.Local, roaming = Paths.Roaming, win = Paths.Windows, profile = Paths.Profile, programData = Paths.ProgramData;
        var list = new List<CleanCategory>
        {
            new()
            {
                Id = "temp.user", Group = "system", DefaultOn = true,
                Title = Loc.T("Kullanıcı geçici dosyaları", "User temporary files"),
                Description = Loc.T("Uygulamaların geride bıraktığı geçici dosyalar. Son 24 saatte değişenlere dokunulmaz; kullanımdaki dosyalar atlanır.",
                                    "Temporary files left behind by apps. Anything changed in the last 24 hours is kept; files in use are skipped."),
                Targets = () =>
                {
                    // Only the standard location is cleaned: a redirected TEMP could be a folder the user works in.
                    string temp = Path.GetTempPath().TrimEnd('\\');
                    string name = Path.GetFileName(temp);
                    return name.Equals("Temp", StringComparison.OrdinalIgnoreCase) || name.Equals("tmp", StringComparison.OrdinalIgnoreCase) ? Existing(new CleanTarget(temp, MinAgeDays: 1)) : [];
                },
            },
            new()
            {
                Id = "temp.windows", Group = "system", DefaultOn = true, RequiresAdmin = true,
                Title = Loc.T("Windows geçici dosyaları", "Windows temporary files"),
                Description = Loc.T("Windows ve kurulum programlarının sistem genelindeki geçici dosyaları (24 saatten eski).", "System-wide temporary files of Windows and installers (older than 24 hours)."),
                Targets = () => Existing(new CleanTarget(Path.Combine(win, "Temp"), MinAgeDays: 1)),
            },
            new()
            {
                Id = "thumbs", Group = "system", DefaultOn = true,
                Title = Loc.T("Küçük resim ve simge önbelleği", "Thumbnail and icon cache"),
                Description = Loc.T("Dosya Gezgini'nin önizleme önbelleği. Windows gerektikçe yeniden oluşturur.", "File Explorer's preview cache. Windows rebuilds it on demand."),
                Targets = () => Existing(new CleanTarget(Path.Combine(local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db", false), new CleanTarget(Path.Combine(local, @"Microsoft\Windows\Explorer"), "iconcache_*.db", false)),
            },
            new()
            {
                Id = "wer", Group = "system", DefaultOn = true,
                Title = Loc.T("Hata raporları", "Error reports"),
                Description = Loc.T("Windows Hata Bildirimi'nin biriktirdiği çökme raporları ve uygulama döküm dosyaları.", "Crash reports and app dump files collected by Windows Error Reporting."),
                Targets = () => Existing(
                    new CleanTarget(Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive")), new CleanTarget(Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue")),
                    new CleanTarget(Path.Combine(local, "CrashDumps")),
                    new CleanTarget(Path.Combine(programData, @"Microsoft\Windows\WER\ReportArchive")), new CleanTarget(Path.Combine(programData, @"Microsoft\Windows\WER\ReportQueue"))),
            },
            new()
            {
                Id = "do.cache", Group = "system", DefaultOn = true, RequiresAdmin = true,
                Title = Loc.T("Teslim İyileştirme önbelleği", "Delivery Optimization cache"),
                Description = Loc.T("Windows Update'in diğer bilgisayarlarla paylaşmak için sakladığı indirilmiş güncelleme parçaları.", "Downloaded update pieces Windows Update keeps to share with other PCs."),
                Targets = () => Existing(new CleanTarget(Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"))),
            },
            new()
            {
                Id = "logs.windows", Group = "system", DefaultOn = true, RequiresAdmin = true,
                Title = Loc.T("Eski Windows günlükleri", "Old Windows logs"),
                Description = Loc.T("Bileşen servisi (CBS) ve DISM günlükleri; 14 günden eski olanlar.", "Component servicing (CBS) and DISM logs older than 14 days."),
                Targets = () => Existing(new CleanTarget(Path.Combine(win, @"Logs\CBS"), MinAgeDays: 14), new CleanTarget(Path.Combine(win, @"Logs\DISM"), MinAgeDays: 14)),
            },
            new()
            {
                Id = "wu.download", Group = "system", RequiresAdmin = true,
                Title = Loc.T("Windows Update indirmeleri", "Windows Update downloads"),
                Description = Loc.T("Kurulmuş güncellemelerin indirme dosyaları (7 günden eski).", "Download files of updates that are already installed (older than 7 days)."),
                Note = Loc.T("Bir güncelleme kurulurken çalıştırmayın. Windows gerekirse dosyaları yeniden indirir.", "Do not run it while an update is installing. Windows downloads the files again if needed."),
                Targets = () => Existing(new CleanTarget(Path.Combine(win, @"SoftwareDistribution\Download"), MinAgeDays: 7)),
            },
            new()
            {
                Id = "dumps.system", Group = "system", RequiresAdmin = true,
                Title = Loc.T("Sistem bellek dökümleri", "System memory dumps"),
                Description = Loc.T("Mavi ekran sonrası yazılan döküm dosyaları (MEMORY.DMP ve minidump).", "Dump files written after a blue screen (MEMORY.DMP and minidumps)."),
                Note = Loc.T("Mavi ekran sorununu inceliyorsanız saklayın: Tanılama sayfası bu dosyaları okur.", "Keep them while investigating a blue screen: the Diagnostics page reads these files."),
                Targets = () => Existing(new CleanTarget(Path.Combine(win, "Minidump")), new CleanTarget(win, "MEMORY.DMP", false)),
            },
            new()
            {
                Id = "shaders", Group = "system",
                Title = Loc.T("Grafik gölgelendirici önbelleği", "Graphics shader cache"),
                Description = Loc.T("DirectX ve ekran kartı sürücüsünün derlenmiş gölgelendirici önbelleği.", "Compiled shader cache of DirectX and the graphics driver."),
                Note = Loc.T("Temizledikten sonra oyunlar gölgelendiricileri yeniden derler; ilk açılışta kısa takılmalar olabilir. Sürücü güncellemesinden sonra ya da grafik hatalarında işe yarar.",
                             "After cleaning, games recompile shaders, which can cause brief stutter on first launch. Useful after a driver update or with graphical glitches."),
                Targets = () => Existing(
                    new CleanTarget(Path.Combine(local, "D3DSCache")), new CleanTarget(Path.Combine(local, @"NVIDIA\DXCache")), new CleanTarget(Path.Combine(local, @"NVIDIA\GLCache")),
                    new CleanTarget(Path.Combine(local, @"AMD\DxCache")), new CleanTarget(Path.Combine(local, @"AMD\DxcCache")), new CleanTarget(Path.Combine(local, @"AMD\VkCache")),
                    new CleanTarget(Path.Combine(Paths.LocalLow, @"Intel\ShaderCache"))),
            },
            new()
            {
                Id = "recyclebin", Group = "system",
                Title = Loc.T("Geri Dönüşüm Kutusu", "Recycle Bin"),
                Description = Loc.T("Sildiğiniz dosyalar. Boşaltıldığında geri getirilemezler.", "Files you deleted. Once emptied they cannot be restored."),
                CustomScan = ShellOps.RecycleBinInfo, CustomClean = ShellOps.EmptyRecycleBin,
            },

            // ---- browsers
            new() { Id = "browser.chrome", Group = "browsers", DefaultOn = true, Title = "Google Chrome", Description = BrowserText, Targets = () => Chromium(Path.Combine(local, @"Google\Chrome\User Data"), "Chrome") },
            new() { Id = "browser.edge", Group = "browsers", DefaultOn = true, Title = "Microsoft Edge", Description = BrowserText, Targets = () => Chromium(Path.Combine(local, @"Microsoft\Edge\User Data"), "Edge") },
            new() { Id = "browser.brave", Group = "browsers", DefaultOn = true, Title = "Brave", Description = BrowserText, Targets = () => Chromium(Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), "Brave") },
            new() { Id = "browser.vivaldi", Group = "browsers", DefaultOn = true, Title = "Vivaldi", Description = BrowserText, Targets = () => Chromium(Path.Combine(local, @"Vivaldi\User Data"), "Vivaldi") },
            new()
            {
                Id = "browser.opera", Group = "browsers", DefaultOn = true, Title = "Opera", Description = BrowserText,
                Targets = () => Existing(
                    new CleanTarget(Path.Combine(local, @"Opera Software\Opera Stable\Cache")), new CleanTarget(Path.Combine(local, @"Opera Software\Opera GX Stable\Cache")),
                    new CleanTarget(Path.Combine(roaming, @"Opera Software\Opera Stable\Code Cache")), new CleanTarget(Path.Combine(roaming, @"Opera Software\Opera Stable\GPUCache")),
                    new CleanTarget(Path.Combine(roaming, @"Opera Software\Opera GX Stable\Code Cache")), new CleanTarget(Path.Combine(roaming, @"Opera Software\Opera GX Stable\GPUCache"))),
            },
            new()
            {
                Id = "browser.firefox", Group = "browsers", DefaultOn = true, Title = "Mozilla Firefox", Description = BrowserText,
                Targets = () =>
                {
                    var list = new List<CleanTarget>();
                    string profiles = Path.Combine(local, @"Mozilla\Firefox\Profiles");
                    if (Directory.Exists(profiles))
                        foreach (var p in Directory.EnumerateDirectories(profiles))
                            foreach (var sub in new[] { "cache2", "startupCache", "thumbnails", "jumpListCache" })
                                if (Directory.Exists(Path.Combine(p, sub))) list.Add(new CleanTarget(Path.Combine(p, sub)));
                    return list;
                },
            },

            // ---- apps
            new()
            {
                Id = "apps.electron", Group = "apps", DefaultOn = true,
                Title = Loc.T("Masaüstü uygulaması önbellekleri", "Desktop app caches"),
                Description = Loc.T("Discord, Slack, VS Code, Teams gibi Electron tabanlı uygulamaların web önbellekleri. Oturumlar ve ayarlar korunur.", "Web caches of Electron-based apps such as Discord, Slack, VS Code and Teams. Sign-ins and settings are kept."),
                Targets = ElectronCaches,
            },
            new()
            {
                Id = "apps.spotify", Group = "apps", DefaultOn = true, Title = "Spotify",
                Description = Loc.T("Çalınan parçaların yerel önbelleği. İndirdiğiniz çevrimdışı müzikler etkilenmez.", "Local cache of streamed tracks. Music downloaded for offline use is not affected."),
                Targets = () => Existing(new CleanTarget(Path.Combine(local, @"Spotify\Data")), new CleanTarget(Path.Combine(local, @"Spotify\Browser\Cache"))),
            },
            new()
            {
                Id = "apps.launchers", Group = "apps", DefaultOn = true,
                Title = Loc.T("Oyun başlatıcı önbellekleri", "Game launcher caches"),
                Description = Loc.T("Steam, Epic Games ve Roblox'un web önbellekleri ve eski günlükleri. Oyun dosyaları ve kayıtlar korunur.", "Web caches and old logs of Steam, Epic Games and Roblox. Game files and saves are kept."),
                Targets = () =>
                {
                    var list = Existing(
                        new CleanTarget(Path.Combine(local, @"EpicGamesLauncher\Saved\webcache")), new CleanTarget(Path.Combine(local, @"EpicGamesLauncher\Saved\Logs"), MinAgeDays: 7),
                        new CleanTarget(Path.Combine(local, @"Roblox\logs"), MinAgeDays: 7), new CleanTarget(Path.Combine(local, @"Steam\htmlcache")));
                    string? steam = Reg.GetStr(@"HKCU\Software\Valve\Steam", "SteamPath")?.Replace('/', '\\');
                    if (steam is not null)
                        list.AddRange(Existing(new CleanTarget(Path.Combine(steam, @"appcache\httpcache")), new CleanTarget(Path.Combine(steam, "logs"), MinAgeDays: 7), new CleanTarget(Path.Combine(steam, "dumps"))));
                    return list;
                },
            },

            // ---- developer
            new() { Id = "dev.npm", Group = "dev", Title = Loc.T("npm önbelleği", "npm cache"), Description = DevText, Targets = () => Existing(new CleanTarget(Path.Combine(local, @"npm-cache\_cacache")), new CleanTarget(Path.Combine(local, @"npm-cache\_logs"))) },
            new() { Id = "dev.pip", Group = "dev", Title = Loc.T("pip önbelleği", "pip cache"), Description = DevText, Targets = () => Existing(new CleanTarget(Path.Combine(local, @"pip\Cache"))) },
            new() { Id = "dev.nuget", Group = "dev", Title = Loc.T("NuGet HTTP önbelleği", "NuGet HTTP cache"), Description = DevText, Targets = () => Existing(new CleanTarget(Path.Combine(local, @"NuGet\v3-cache")), new CleanTarget(Path.Combine(local, @"NuGet\plugins-cache"))) },
            new() { Id = "dev.yarn", Group = "dev", Title = Loc.T("Yarn önbelleği", "Yarn cache"), Description = DevText, Targets = () => Existing(new CleanTarget(Path.Combine(local, @"Yarn\Cache"))) },
            new() { Id = "dev.gradle", Group = "dev", Title = Loc.T("Gradle önbelleği", "Gradle caches"), Description = DevText, Targets = () => Existing(new CleanTarget(Path.Combine(profile, @".gradle\caches"))) },
            new() { Id = "dev.cargo", Group = "dev", Title = Loc.T("Cargo kayıt önbelleği", "Cargo registry cache"), Description = DevText, Targets = () => Existing(new CleanTarget(Path.Combine(profile, @".cargo\registry\cache"))) },
            new() { Id = "dev.go", Group = "dev", Title = Loc.T("Go derleme önbelleği", "Go build cache"), Description = DevText, Targets = () => Existing(new CleanTarget(Path.Combine(local, "go-build"))) },
        };
        return list;
    }

    private static readonly LText BrowserText = Loc.T("Web önbelleği ve derlenmiş kod önbelleği. Geçmiş, çerezler, parolalar ve açık sekmeler korunur. Tarayıcı açıksa kullanımdaki dosyalar atlanır.",
                                                      "Web cache and compiled code cache. History, cookies, passwords and open tabs are kept. If the browser is open, files in use are skipped.");
    private static readonly LText DevText = Loc.T("İndirilmiş paketlerin önbelleği. Temizlendiğinde paketler bir sonraki kurulumda yeniden indirilir.",
                                                  "Cache of downloaded packages. After cleaning, packages are downloaded again on the next install.");

    // ------------------------------------------------------------------ scan / clean
    public CleanScan Scan(JobContext? job)
    {
        var scan = new CleanScan { Time = DateTime.UtcNow };
        for (int i = 0; i < _catalog.Count; i++)
        {
            var c = _catalog[i];
            job?.ThrowIfCancelled();
            job?.Report(i / (double)_catalog.Count, Loc.T("Taranıyor: {0}", "Scanning: {0}").F(c.Title));
            var r = new CategoryResult
            {
                Id = c.Id, Group = c.Group, Title = c.Title, Description = c.Description, Note = c.Note, RequiresAdmin = c.RequiresAdmin,
                DefaultOn = c.DefaultOn, Available = !c.RequiresAdmin || Elevation.IsElevated,
            };
            try
            {
                if (c.CustomScan is not null) (r.Bytes, r.Files) = c.CustomScan();
                else
                {
                    var largest = new PriorityQueue<FileEntry, long>();
                    var parts = new Dictionary<string, (long Bytes, long Files)>();
                    foreach (var target in c.Targets())
                        foreach (var f in FileCleaner.Enumerate(target))
                        {
                            long size;
                            try { size = f.Length; } catch { continue; }
                            r.Bytes += size;
                            r.Files++;
                            if (target.Label is not null) { var p = parts.GetValueOrDefault(target.Label); parts[target.Label] = (p.Bytes + size, p.Files + 1); }
                            largest.Enqueue(new FileEntry(f.FullName, size, f.LastWriteTimeUtc), size);
                            if (largest.Count > 25) largest.Dequeue();
                            if ((r.Files & 0x3FF) == 0) job?.ThrowIfCancelled();
                        }
                    r.Largest = [.. largest.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Bytes)];
                    r.Parts = [.. parts.Select(p => new SubTotal(p.Key, p.Value.Bytes, p.Value.Files)).OrderByDescending(p => p.Bytes)];
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Log.Warn("cleanup: scan " + c.Id, e); }
            if (r.Bytes > 0 || c.Group == "system") scan.Categories.Add(r);
        }
        scan.TotalBytes = scan.Categories.Sum(c => c.Bytes);
        scan.RecommendedBytes = scan.Categories.Where(c => c.DefaultOn && c.Available).Sum(c => c.Bytes);
        _last = scan;
        _store.Save(Path.Combine("cleanup", "last-scan.json"), scan);
        return scan;
    }

    public object Clean(string[] ids, JobContext job)
    {
        var chosen = _catalog.Where(c => ids.Contains(c.Id)).ToList();
        if (chosen.Count == 0) throw RpcException.Invalid("No category selected");
        if (chosen.Any(c => c.RequiresAdmin) && !Elevation.IsElevated) throw RpcException.Elevation();

        bool dry = _journal.DryRun;
        long freed = 0, deleted = 0, skipped = 0;
        var per = new List<object>();
        using var scope = _journal.Begin("cleanup", Loc.T("Temizlik: {0} kategori", "Cleanup: {0} categories").F(chosen.Count));
        for (int i = 0; i < chosen.Count; i++)
        {
            var c = chosen[i];
            job.ThrowIfCancelled();
            job.Report(i / (double)chosen.Count, Loc.T("Temizleniyor: {0}", "Cleaning: {0}").F(c.Title));
            long bytes = 0, files = 0, skip = 0;
            if (c.CustomScan is not null)
            {
                (bytes, files) = c.CustomScan();
                if (!dry) c.CustomClean?.Invoke();
            }
            else
                foreach (var target in c.Targets())
                {
                    var (b, f, s) = FileCleaner.Delete(target, dry, job.Token);
                    bytes += b; files += f; skip += s;
                }
            freed += bytes; deleted += files; skipped += skip;
            per.Add(new { id = c.Id, title = c.Title, bytes, files, skipped = skip });
            scope.Note("files.deleted", c.Id, new() { ["bytes"] = bytes.ToString(), ["files"] = files.ToString(), ["skipped"] = skip.ToString() });
        }
        scope.Set.BytesFreed = dry ? 0 : freed;
        var run = new CleanRun { Time = DateTime.UtcNow, FreedBytes = freed, Deleted = deleted, Skipped = skipped, Simulated = dry, Categories = [.. chosen.Select(c => c.Id)] };
        _store.Append(Path.Combine("cleanup", "history.jsonl"), run);
        if (!dry) _last = null;
        return new { freedBytes = freed, deleted, skipped, simulated = dry, categories = per };
    }

    // ------------------------------------------------------------------ user file tools
    public static List<string> DefaultRoots()
    {
        var roots = new List<string>();
        foreach (var f in new[] { Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic })
        {
            string p = Environment.GetFolderPath(f);
            if (p.Length > 0 && Directory.Exists(p)) roots.Add(p);
        }
        string downloads = Path.Combine(Paths.Profile, "Downloads");
        if (Directory.Exists(downloads)) roots.Add(downloads);
        return [.. roots.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static List<string> Roots(RpcContext c)
    {
        var roots = c.StrArr("roots").Where(Directory.Exists).ToList();
        return roots.Count > 0 ? roots : DefaultRoots();
    }

    private static IEnumerable<FileInfo> UserFiles(IEnumerable<string> roots, JobContext job)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline,
        };
        long seen = 0;
        foreach (var root in roots)
        {
            IEnumerator<FileInfo> e;
            try { e = new DirectoryInfo(root).EnumerateFiles("*", options).GetEnumerator(); } catch { continue; }
            using (e)
                while (true)
                {
                    try { if (!e.MoveNext()) break; } catch { break; }
                    if ((++seen & 0x1FF) == 0) { job.ThrowIfCancelled(); job.Say(Loc.T("{0:N0} dosya incelendi…", "{0:N0} files examined…").F(seen)); }
                    yield return e.Current;
                }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes; public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

    /// <summary>Identity of the data on disk. Two paths with the same identity are hard links, not duplicates.</summary>
    private static (uint Volume, ulong Index)? FileIdentity(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(h, out var i) ? (i.VolumeSerialNumber, ((ulong)i.FileIndexHigh << 32) | i.FileIndexLow) : null;
        }
        catch { return null; }
    }

    private static string? Hash(string path, long limit, CancellationToken ct, Action<long>? progress = null)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            long left = limit;
            while (left > 0)
            {
                ct.ThrowIfCancellationRequested();
                int read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read <= 0) break;
                sha.AppendData(buffer, 0, read);
                left -= read;
                progress?.Invoke(read);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private object FindDuplicates(List<string> roots, long minSize, JobContext job)
    {
        job.Report(-1, Loc.T("Dosyalar listeleniyor…", "Listing files…"));
        var bySize = new Dictionary<long, List<FileInfo>>();
        foreach (var f in UserFiles(roots, job))
        {
            long size;
            try { size = f.Length; } catch { continue; }
            if (size < minSize) continue;
            if (!bySize.TryGetValue(size, out var list)) bySize[size] = list = [];
            list.Add(f);
        }
        var candidates = bySize.Where(g => g.Value.Count > 1).ToList();

        // Stage 1: compare the first 128 KB. Stage 2: full content hash for the files that still collide.
        var stage2 = new List<(long Size, List<FileInfo> Files)>();
        int step = 0;
        foreach (var (size, files) in candidates)
        {
            job.ThrowIfCancelled();
            job.Report(0.3 * ++step / candidates.Count, Loc.T("Aday dosyalar karşılaştırılıyor…", "Comparing candidate files…"));
            foreach (var g in files.GroupBy(f => Hash(f.FullName, 128 << 10, job.Token)).Where(g => g.Key is not null && g.Count() > 1))
                stage2.Add((size, [.. g]));
        }
        long totalBytes = stage2.Sum(s => s.Size * s.Files.Count), doneBytes = 0;
        var groups = new List<object>();
        long wasted = 0;
        foreach (var (size, files) in stage2)
        {
            foreach (var g in files.GroupBy(f => size <= 128 << 10 ? "same" : Hash(f.FullName, long.MaxValue, job.Token, n =>
                     {
                         doneBytes += n;
                         job.Report(0.3 + 0.7 * doneBytes / Math.Max(1, totalBytes), Loc.T("İçerik doğrulanıyor: {0} / {1}", "Verifying content: {0} / {1}").F(new ByteSize(doneBytes), new ByteSize(totalBytes)));
                     })).Where(g => g.Key is not null && g.Count() > 1))
            {
                // Collapse hard links: they share one copy of the data.
                var distinct = g.GroupBy(f => FileIdentity(f.FullName) is { } id ? id.ToString() : f.FullName).Select(x => x.First()).ToList();
                if (distinct.Count < 2) continue;
                wasted += size * (distinct.Count - 1);
                groups.Add(new
                {
                    size, hash = g.Key == "same" ? null : g.Key![..16], wasted = size * (distinct.Count - 1),
                    files = distinct.OrderBy(f => f.CreationTimeUtc).Select(f => new { path = f.FullName, modified = f.LastWriteTimeUtc, created = f.CreationTimeUtc }).ToList(),
                });
            }
        }
        return new { groups = groups.OrderByDescending(g => ((dynamic)g).wasted).Take(500).ToList(), wasted, count = groups.Count, roots };
    }

    private static object FindLarge(List<string> roots, long minSize, int olderThanDays, JobContext job)
    {
        job.Report(-1);
        DateTime cutoff = olderThanDays > 0 ? DateTime.UtcNow.AddDays(-olderThanDays) : DateTime.MaxValue;
        var top = new PriorityQueue<FileInfo, long>();
        long total = 0, count = 0;
        foreach (var f in UserFiles(roots, job))
        {
            long size;
            try { size = f.Length; if (size < minSize || f.LastWriteTimeUtc > cutoff) continue; } catch { continue; }
            total += size; count++;
            top.Enqueue(f, size);
            if (top.Count > 400) top.Dequeue();
        }
        return new
        {
            total, count, roots,
            files = top.UnorderedItems.Select(x => x.Element).OrderByDescending(f => f.Length)
                .Select(f => new { path = f.FullName, bytes = f.Length, modified = f.LastWriteTimeUtc, accessed = f.LastAccessTimeUtc, ext = f.Extension.ToLowerInvariant() }).ToList(),
        };
    }

    private static object FindEmptyFolders(List<string> roots, JobContext job)
    {
        job.Report(-1);
        var empty = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        long seen = 0;
        foreach (var root in roots)
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root, "*", options); } catch { continue; }
            foreach (var dir in dirs)
            {
                if ((++seen & 0xFF) == 0) { job.ThrowIfCancelled(); job.Say(Loc.T("{0:N0} klasör incelendi…", "{0:N0} folders examined…").F(seen)); }
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) empty.Add(dir); } catch { /* unreadable */ }
                if (empty.Count >= 2000) break;
            }
        }
        return new { folders = empty, roots };
    }

    /// <summary>
    /// Decides whether a folder is regenerable build output. A name alone is never enough: "bin" holds
    /// source scripts in many projects, so each kind must be confirmed by the project file next to it.
    /// </summary>
    /// <returns>null when it is not an artifact; otherwise whether the classification is certain.</returns>
    internal static bool? ArtifactConfidence(string folder)
    {
        string name = Path.GetFileName(folder.TrimEnd('\\')).ToLowerInvariant();
        string? parent = Path.GetDirectoryName(folder.TrimEnd('\\'));
        if (parent is null) return null;
        bool Has(params string[] patterns) => patterns.Any(p => { try { return Directory.EnumerateFiles(parent, p, FileCleaner.Flat).Any(); } catch { return false; } });
        return name switch
        {
            "node_modules" => true,
            "__pycache__" or ".pytest_cache" or ".mypy_cache" => true,
            ".venv" or "venv" => File.Exists(Path.Combine(folder, "pyvenv.cfg")) ? true : null,
            "bin" or "obj" => Has("*.csproj", "*.fsproj", "*.vbproj") ? true : null,
            "target" => Has("Cargo.toml", "pom.xml") ? true : null,
            ".gradle" => Has("build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts") ? true : null,
            ".next" or ".nuxt" or ".turbo" or ".parcel-cache" => Has("package.json") ? true : null,
            "dist" => Has("package.json") ? false : null, // usually output, but some projects commit it: listed, never preselected
            _ => null,
        };
    }

    /// <summary>node_modules, build output and virtual environments of projects nobody touched for a while.</summary>
    private static object FindStaleArtifacts(List<string> roots, int idleDays, JobContext job)
    {
        job.Report(-1);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules", ".venv", "venv", "__pycache__", "target", ".gradle", ".next", ".nuxt", ".turbo", ".parcel-cache", "bin", "obj", "dist", ".pytest_cache", ".mypy_cache" };
        var found = new List<object>();
        long total = 0, seen = 0;
        DateTime cutoff = DateTime.UtcNow.AddDays(-idleDays);
        var stack = new Stack<(string Dir, int Depth)>(roots.Select(r => (r, 0)));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            if ((++seen & 0x7F) == 0) { job.ThrowIfCancelled(); job.Say(Loc.T("{0:N0} klasör incelendi…", "{0:N0} folders examined…").F(seen)); }
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir, "*", FileCleaner.Flat); } catch { continue; }
            foreach (var child in children)
            {
                string name = Path.GetFileName(child);
                if (!names.Contains(name)) { if (depth < 7 && !name.StartsWith('.') && name != "AppData") stack.Push((child, depth + 1)); continue; }
                try
                {
                    bool? sure = ArtifactConfidence(child);
                    if (sure is null) { if (depth < 7 && !name.StartsWith('.')) stack.Push((child, depth + 1)); continue; }
                    // The project counts as idle when none of its own files changed recently.
                    DateTime latest = Directory.EnumerateFiles(dir, "*", FileCleaner.Flat).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(dir)).Max();
                    if (latest > cutoff) continue;
                    long size = 0, files = 0;
                    foreach (var f in new DirectoryInfo(child).EnumerateFiles("*", FileCleaner.Walk)) { size += f.Length; files++; if ((files & 0x3FF) == 0) job.ThrowIfCancelled(); }
                    if (size < 1 << 20) continue;
                    total += size;
                    found.Add(new { path = child, project = dir, kind = name, bytes = size, files, idleDays = (int)(DateTime.UtcNow - latest).TotalDays, sure = sure.Value });
                }
                catch (OperationCanceledException) { throw; }
                catch { /* unreadable */ }
            }
        }
        return new { items = found.OrderByDescending(x => ((dynamic)x).bytes).Take(500).ToList(), total, roots };
    }

    private object DeletePaths(string[] paths, bool recycle, string kind)
    {
        int ok = 0, failed = 0;
        long bytes = 0;
        using var scope = _journal.Begin("cleanup", recycle
            ? Loc.T("{0} öğe Geri Dönüşüm Kutusu'na taşındı", "{0} items moved to the Recycle Bin").F(paths.Length)
            : Loc.T("{0} öğe silindi", "{0} items deleted").F(paths.Length));
        foreach (var path in paths)
        {
            try
            {
                bool isDir = Directory.Exists(path);
                if (!isDir && !File.Exists(path)) { failed++; continue; }
                if (!FileCleaner.IsSafeRoot(path)) { failed++; continue; }
                long size = isDir ? 0 : new FileInfo(path).Length;
                if (kind == "empty" && (!isDir || Directory.EnumerateFileSystemEntries(path).Any())) { failed++; continue; }
                scope.Note(recycle ? "files.recycled" : "files.deleted", path, new() { ["bytes"] = size.ToString() });
                if (!scope.DryRun)
                {
                    if (recycle) { if (!ShellOps.Recycle(path)) { failed++; continue; } }
                    else if (isDir) Directory.Delete(path, kind != "empty");
                    else File.Delete(path);
                }
                ok++;
                bytes += size;
            }
            catch { failed++; }
        }
        scope.Set.BytesFreed = scope.DryRun || recycle ? 0 : bytes;
        return new { done = ok, failed, bytes, simulated = scope.DryRun };
    }

    public IEnumerable<Finding> Findings()
    {
        if (_last is null || _last.RecommendedBytes < 1L << 30) yield break;
        bool big = _last.RecommendedBytes > 10L << 30;
        yield return new Finding
        {
            Id = "cleanup.junk", Group = "maintenance", Severity = big ? Severity.Warning : Severity.Info, Penalty = big ? 6 : 0,
            Title = Loc.T("{0} güvenle temizlenebilir", "{0} can be cleaned safely").F(new ByteSize(_last.RecommendedBytes)),
            Detail = Loc.T("Geçici dosyalar ve yeniden oluşturulabilen önbellekler. Kişisel dosyalarınıza, tarayıcı geçmişine ya da oturumlara dokunulmaz.",
                           "Temporary files and caches that can be rebuilt. Your personal files, browser history and sign-ins are not touched."),
            Impact = Loc.T("{0} boş alan kazanılır.", "Frees {0} of disk space.").F(new ByteSize(_last.RecommendedBytes)),
        }.Ev(Loc.T("Son tarama", "Last scan"), Loc.T("{0:dd.MM.yyyy HH:mm}", "{0:yyyy-MM-dd HH:mm}").F(_last.Time.ToLocalTime()))
         .Go(Loc.T("Temizliği aç", "Open cleanup"), "cleanup");
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("cleanup.scan", _ => _jobs.Start("cleanup.scan", Loc.T("Temizlik taraması", "Cleanup scan"), Scan));
        rpc.On("cleanup.last", _ => _last);
        rpc.On("cleanup.clean", c =>
        {
            var ids = c.StrArr("ids");
            return _jobs.Start("cleanup.clean", Loc.T("Temizlik", "Cleanup"), job => Clean(ids, job));
        });
        rpc.On("cleanup.history", _ => _store.ReadLines<CleanRun>(Path.Combine("cleanup", "history.jsonl")).TakeLast(60).Reverse().ToList());
        rpc.On("cleanup.roots", _ => DefaultRoots());
        rpc.On("cleanup.duplicates", c =>
        {
            var roots = Roots(c);
            long min = Math.Max(1024, c.Long("minSize", 1 << 20));
            return _jobs.Start("cleanup.duplicates", Loc.T("Yinelenen dosya taraması", "Duplicate file scan"), job => FindDuplicates(roots, min, job));
        });
        rpc.On("cleanup.large", c =>
        {
            var roots = Roots(c);
            long min = Math.Max(1 << 20, c.Long("minSize", 100 << 20));
            int days = c.Int("olderThanDays");
            return _jobs.Start("cleanup.large", Loc.T("Büyük dosya taraması", "Large file scan"), job => FindLarge(roots, min, days, job));
        });
        rpc.On("cleanup.empty", c =>
        {
            var roots = Roots(c);
            return _jobs.Start("cleanup.empty", Loc.T("Boş klasör taraması", "Empty folder scan"), job => FindEmptyFolders(roots, job));
        });
        rpc.On("cleanup.artifacts", c =>
        {
            var roots = c.StrArr("roots").Where(Directory.Exists).ToList();
            if (roots.Count == 0) roots = [.. new[] { "source", "repos", "dev", "code", "projects", "Projects", "Desktop", "Documents" }.Select(n => Path.Combine(Paths.Profile, n)).Where(Directory.Exists)];
            int idle = Math.Max(0, c.Int("idleDays", 30));
            return _jobs.Start("cleanup.artifacts", Loc.T("Proje artıkları taraması", "Project leftovers scan"), job => FindStaleArtifacts(roots, idle, job));
        });
        rpc.On("cleanup.recycle", c => DeletePaths(c.StrArr("paths"), recycle: true, "files"));
        rpc.On("cleanup.deleteEmpty", c => DeletePaths(c.StrArr("paths"), recycle: false, "empty"));
        rpc.On("cleanup.deleteArtifacts", c =>
        {
            var paths = c.StrArr("paths");
            // Re-verified at deletion time: the folder must still qualify as build output.
            if (paths.Any(p => ArtifactConfidence(p) is null)) throw RpcException.Denied(Loc.S("Yalnızca derleme çıktısı klasörleri silinebilir.", "Only build output folders can be deleted here."));
            return DeletePaths(paths, recycle: false, "artifacts");
        });
    }
}
