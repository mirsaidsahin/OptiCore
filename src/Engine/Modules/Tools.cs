using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public sealed class SystemSnapshot
{
    public string Id { get; set; } = "";
    public DateTime Time { get; set; }
    public string Label { get; set; } = "";
    public Dictionary<string, string> Apps { get; set; } = [];
    public Dictionary<string, bool> Startup { get; set; } = [];
    public Dictionary<string, string> Services { get; set; } = [];
    public Dictionary<string, string> Drivers { get; set; } = [];
    public List<string> Tweaks { get; set; } = [];
    public Dictionary<string, string> System { get; set; } = [];
}

/// <summary>Toolbox: context menu manager, file shredder, file locks, system snapshots, duplicate runtimes, file mover.</summary>
public sealed partial class ToolsService
{
    private readonly ChangeJournal _journal;
    private readonly DataStore _store;
    private readonly AppsService _apps;
    private readonly StartupService _startup;
    private readonly DriverService _drivers;
    private readonly TweakService _tweaks;
    private readonly SystemInfoService _system;
    private readonly JobManager _jobs;

    public ToolsService(ChangeJournal journal, DataStore store, AppsService apps, StartupService startup, DriverService drivers, TweakService tweaks, SystemInfoService system, JobManager jobs)
    {
        _journal = journal; _store = store; _apps = apps; _startup = startup; _drivers = drivers; _tweaks = tweaks; _system = system; _jobs = jobs;
        journal.RegisterUndo("file.move", e =>
        {
            string from = e.After["to"]!, to = e.Before["from"]!;
            if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
        });
    }

    // ------------------------------------------------------------------ context menu
    private static readonly (string Root, LText Where)[] MenuRoots =
    [
        (@"HKCR\*\shell", Loc.T("Tüm dosyalar", "All files")), (@"HKCR\*\shellex\ContextMenuHandlers", Loc.T("Tüm dosyalar (eklenti)", "All files (extension)")),
        (@"HKCR\Directory\shell", Loc.T("Klasörler", "Folders")), (@"HKCR\Directory\shellex\ContextMenuHandlers", Loc.T("Klasörler (eklenti)", "Folders (extension)")),
        (@"HKCR\Directory\Background\shell", Loc.T("Klasör arka planı", "Folder background")), (@"HKCR\Directory\Background\shellex\ContextMenuHandlers", Loc.T("Klasör arka planı (eklenti)", "Folder background (extension)")),
        (@"HKCR\Drive\shell", Loc.T("Sürücüler", "Drives")), (@"HKCR\DesktopBackground\Shell", Loc.T("Masaüstü", "Desktop")),
    ];

    // Built-in Windows verbs that must stay.
    private static readonly HashSet<string> CoreVerbs = new(StringComparer.OrdinalIgnoreCase) { "open", "opennewwindow", "opennewtab", "find", "explore", "runas", "edit", "print", "printto", "cmd", "Powershell", "properties", "Sharing", "{596AB062-B4D2-4215-9F74-E9109B0A8153}", "EnhancedStorageShell", "pintohome", "UpdateEncryptionSettings", "ModernSharing", "Library Location", "WorkFolders", "CopyAsPathMenu", "SendTo", "PasswordMenu", "Display", "Personalize", "DesktopBackground" };

    private static string? Resolve(string text) => text.StartsWith('@') ? ShellOps.IndirectString(text) : text;

    private object MenuItems()
    {
        var items = new List<object>();
        foreach (var (root, where) in MenuRoots)
        {
            bool shellex = root.Contains("shellex", StringComparison.OrdinalIgnoreCase);
            foreach (var name in Reg.SubKeys(root))
            {
                string key = root + "\\" + name;
                if (CoreVerbs.Contains(name.TrimStart('-'))) continue;
                string label;
                string? command = null;
                bool enabled;
                if (shellex)
                {
                    string clsid = name.StartsWith('{') ? name.TrimStart('-') : Reg.GetStr(key, null) ?? "";
                    enabled = !name.StartsWith('-') && !(Reg.GetStr(key, null) ?? "").StartsWith('-');
                    label = Reg.GetStr(@"HKCR\CLSID\" + clsid.TrimStart('-'), null) is { Length: > 0 } n ? n : name.TrimStart('-');
                    command = Reg.GetStr(@"HKCR\CLSID\" + clsid.TrimStart('-') + @"\InprocServer32", null);
                }
                else
                {
                    string? mui = Reg.GetStr(key, "MUIVerb") ?? Reg.GetStr(key, null);
                    label = (mui is null ? null : Resolve(mui))?.Replace("&", "") is { Length: > 0 } l ? l : name;
                    command = Reg.GetStr(key + @"\command", null);
                    enabled = Reg.Get(key, "LegacyDisable") is null && Reg.Get(key, "ProgrammaticAccessOnly") is null;
                }
                items.Add(new { id = key, root, where, name, label, command, enabled, kind = shellex ? "extension" : "verb", admin = !Reg.Exists(key.Replace(@"HKCR\", @"HKCU\Software\Classes\")) });
            }
        }
        return items;
    }

    private object SetMenuItem(string key, bool enabled)
    {
        if (!MenuRoots.Any(r => key.StartsWith(r.Root + "\\", StringComparison.OrdinalIgnoreCase))) throw RpcException.Denied("Not a context menu entry");
        string name = key[(key.LastIndexOf('\\') + 1)..];
        if (CoreVerbs.Contains(name)) throw RpcException.Denied("Windows core entry");
        // HKCR is a merged view: writes go to HKLM\SOFTWARE\Classes unless the key lives in HKCU.
        string userKey = key.Replace(@"HKCR\", @"HKCU\Software\Classes\");
        string target = Reg.Exists(userKey) ? userKey : key.Replace(@"HKCR\", @"HKLM\SOFTWARE\Classes\");
        using var scope = _journal.Begin("tools", (enabled ? Loc.T("Sağ tık menüsünde açıldı: {0}", "Context menu entry enabled: {0}") : Loc.T("Sağ tık menüsünden kaldırıldı: {0}", "Context menu entry hidden: {0}")).F(name));
        ChangeEntry entry;
        if (key.Contains("shellex", StringComparison.OrdinalIgnoreCase))
        {
            // Shell extensions are disabled by prefixing their CLSID default value with "-" (the way ShellExView does it).
            string? value = Reg.GetStr(target, null);
            if (value is null || !value.TrimStart('-').StartsWith('{')) throw RpcException.Unsupported(Loc.S("Bu eklenti bu yolla kapatılamıyor.", "This extension cannot be switched this way."));
            entry = scope.RegSet(target, "", RegistryValueKind.String, enabled ? value.TrimStart('-') : "-" + value.TrimStart('-'));
        }
        else entry = enabled ? scope.RegDeleteValue(target, "LegacyDisable") : scope.RegSet(target, "LegacyDisable", RegistryValueKind.String, "");
        scope.Commit();
        if (entry.Error == "ELEVATION_REQUIRED") throw RpcException.Elevation();
        if (entry.Error is not null) throw RpcException.Failed(entry.Error);
        return new { simulated = scope.DryRun };
    }

    // ------------------------------------------------------------------ shredder
    private object Shred(string[] paths, int passes, JobContext job)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (!FileCleaner.IsSafeRoot(p)) throw RpcException.Denied(Loc.S("Sistem klasörleri ve kullanıcı ana klasörleri imha edilemez.", "System folders and top-level user folders cannot be shredded."));
            if (File.Exists(p)) files.Add(p);
            else if (Directory.Exists(p)) files.AddRange(Directory.EnumerateFiles(p, "*", FileCleaner.Walk));
        }
        long total = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }), done = 0;
        int ok = 0, failed = 0;
        using var scope = _journal.Begin("tools", Loc.T("{0} dosya güvenli silindi", "{0} files shredded").F(files.Count));
        var buffer = new byte[1 << 20];
        foreach (var f in files)
        {
            job.ThrowIfCancelled();
            scope.Note("file.shred", f);
            if (scope.DryRun) { ok++; continue; }
            try
            {
                File.SetAttributes(f, FileAttributes.Normal);
                long len = new FileInfo(f).Length;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.WriteThrough))
                    for (int pass = 0; pass < passes; pass++)
                    {
                        fs.Position = 0;
                        for (long off = 0; off < len; off += buffer.Length)
                        {
                            RandomNumberGenerator.Fill(buffer);
                            int n = (int)Math.Min(buffer.Length, len - off);
                            fs.Write(buffer, 0, n);
                            done += n / passes;
                            job.Report(total > 0 ? done / (double)total : -1);
                        }
                        fs.Flush(true);
                    }
                // Rename before deleting so the original name does not survive in the file table.
                string renamed = Path.Combine(Path.GetDirectoryName(f)!, Guid.NewGuid().ToString("N")[..12]);
                File.Move(f, renamed);
                File.Delete(renamed);
                ok++;
            }
            catch { failed++; }
        }
        foreach (var p in paths.Where(Directory.Exists).OrderByDescending(p => p.Length))
            try { if (!scope.DryRun) Directory.Delete(p, true); } catch { /* files that failed remain */ }
        return new { shredded = ok, failed, simulated = scope.DryRun };
    }

    // ------------------------------------------------------------------ file mover
    private object Move(string[] paths, string destination)
    {
        if (!Directory.Exists(destination)) throw RpcException.NotFound(Loc.S("Hedef klasör bulunamadı.", "Destination folder not found."));
        int ok = 0, failed = 0;
        long bytes = 0;
        using var scope = _journal.Begin("tools", Loc.T("{0} öğe {1} konumuna taşındı", "{0} items moved to {1}").F(paths.Length, destination));
        foreach (var p in paths)
        {
            if (!FileCleaner.IsSafeRoot(p)) { failed++; continue; }
            string to = Path.Combine(destination, Path.GetFileName(p.TrimEnd('\\')));
            if (File.Exists(to) || Directory.Exists(to)) { failed++; continue; }
            long size = File.Exists(p) ? new FileInfo(p).Length : 0;
            var e = scope.Custom("file.move", p, new() { ["from"] = p }, new() { ["to"] = to }, () =>
            {
                if (Directory.Exists(p))
                {
                    if (Path.GetPathRoot(p)!.Equals(Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)) Directory.Move(p, to);
                    else throw new IOException(Loc.S("Klasörler yalnızca aynı sürücü içinde taşınabilir.", "Folders can only be moved within the same drive."));
                }
                else File.Move(p, to);
            });
            if (e.Error is null) { ok++; bytes += size; } else failed++;
        }
        return new { moved = ok, failed, bytes, simulated = scope.DryRun };
    }

    // ------------------------------------------------------------------ snapshots
    private SystemSnapshot Capture(string label, JobContext job)
    {
        var hw = _system.Hardware(true);
        job.Report(0.1, Loc.T("Uygulamalar…", "Apps…"));
        var snap = new SystemSnapshot { Id = DateTime.Now.ToString("yyyyMMdd-HHmmss"), Time = DateTime.UtcNow, Label = label };
        foreach (var a in _apps.List(true)) snap.Apps[a.Name] = a.Version ?? "";
        job.Report(0.35, Loc.T("Başlangıç öğeleri ve hizmetler…", "Startup items and services…"));
        foreach (var e in _startup.List(true)) snap.Startup[e.Kind + ": " + e.Name] = e.Enabled;
        foreach (var row in Wmi.Query("SELECT Name, StartMode FROM Win32_Service")) snap.Services[row.S("Name")] = row.S("StartMode");
        job.Report(0.6, Loc.T("Sürücüler…", "Drivers…"));
        foreach (var d in _drivers.List(true).Where(d => d.Version is not null)) snap.Drivers[d.Device] = d.Version!;
        snap.Tweaks = _tweaks.AppliedIds();
        snap.System = new()
        {
            ["Windows"] = $"{hw.Os.Name} {hw.Os.Version} ({hw.Os.Build})", ["CPU"] = hw.Cpu.Name, ["RAM"] = new ByteSize(hw.Memory.InstalledBytes).ToString(),
            ["GPU"] = string.Join(", ", hw.Gpus.Select(g => $"{g.Name} {g.DriverVersion}")), ["BIOS"] = hw.Board.BiosVersion,
            ["PowerPlan"] = PowerPlans.List().FirstOrDefault(p => p.Active)?.Name ?? "",
        };
        _store.Save(Path.Combine("snapshots", snap.Id + ".json"), snap);
        return snap;
    }

    private List<SystemSnapshot> Snapshots() =>
        [.. Directory.EnumerateFiles(_store.DirOf("snapshots"), "*.json").OrderByDescending(f => f)
            .Select(f => _store.Load<SystemSnapshot?>(Path.Combine("snapshots", Path.GetFileName(f)), () => null)).OfType<SystemSnapshot>()];

    private static object DiffMaps<T>(Dictionary<string, T> a, Dictionary<string, T> b) => new
    {
        added = b.Keys.Except(a.Keys).OrderBy(k => k).Select(k => new { name = k, value = b[k]?.ToString() }).ToList(),
        removed = a.Keys.Except(b.Keys).OrderBy(k => k).Select(k => new { name = k, value = a[k]?.ToString() }).ToList(),
        changed = a.Keys.Intersect(b.Keys).Where(k => !Equals(a[k], b[k])).OrderBy(k => k).Select(k => new { name = k, from = a[k]?.ToString(), to = b[k]?.ToString() }).ToList(),
    };

    private object Diff(string fromId, string toId)
    {
        var all = Snapshots();
        var a = all.FirstOrDefault(s => s.Id == fromId) ?? throw RpcException.NotFound("snapshot");
        var b = toId == "now" ? Capture("", new JobContext(new Job { Id = "diff" })) : all.FirstOrDefault(s => s.Id == toId) ?? throw RpcException.NotFound("snapshot");
        if (toId == "now") File.Delete(_store.PathOf("snapshots", b.Id + ".json"));
        return new
        {
            from = new { a.Id, a.Time, a.Label }, to = new { b.Id, b.Time, b.Label },
            apps = DiffMaps(a.Apps, b.Apps), startup = DiffMaps(a.Startup, b.Startup), services = DiffMaps(a.Services, b.Services),
            drivers = DiffMaps(a.Drivers, b.Drivers), system = DiffMaps(a.System, b.System),
            tweaks = DiffMaps(a.Tweaks.ToDictionary(x => x, _ => "on"), b.Tweaks.ToDictionary(x => x, _ => "on")),
        };
    }

    // ------------------------------------------------------------------ duplicate runtimes
    [GeneratedRegex(@"(\d+)\.(\d+)")]
    private static partial Regex VersionPattern();

    private static string? MajorMinor(string? text)
    {
        if (text is null) return null;
        var m = VersionPattern().Match(text);
        return m.Success ? m.Groups[1].Value + "." + m.Groups[2].Value : null;
    }

    private object Duplicates()
    {
        // Several versions of the same runtime or tool are often left behind by installers.
        string[] families = ["Java", "Python", "Node.js", "Microsoft .NET", "Microsoft Visual C++", "Microsoft Edge WebView2", "Adobe AIR", "Git", "7-Zip", "VLC", "Zoom", "Microsoft Teams", "Steam", "NVIDIA PhysX", "Visual Studio Code"];
        var apps = _apps.List().Where(a => a.Kind == "win32").ToList();
        var groups = new List<object>();
        foreach (var fam in families)
        {
            var matches = apps.Where(a => a.Name.Equals(fam, StringComparison.OrdinalIgnoreCase) || a.Name.StartsWith(fam + " ", StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count < 2) continue;
            bool normal = fam is "Microsoft Visual C++" or "Microsoft .NET";
            // Components of one product share its version (Python's installer registers a dozen): only distinct major.minor versions count.
            int versions = matches.Select(a => MajorMinor(a.Version) ?? MajorMinor(a.Name)).Where(v => v is not null).Distinct().Count();
            if (!normal && versions < 2) continue;
            groups.Add(new
            {
                family = fam, normal,
                note = normal ? Loc.T("Farklı programlar farklı sürümlere ihtiyaç duyar; bunları kaldırmayın.", "Different programs need different versions; do not remove these.")
                    : Loc.T("Birden fazla sürüm kurulu. Genellikle yalnızca en yenisi gerekir; eskisini kullanan bir program olmadığından eminseniz kaldırabilirsiniz.", "Several versions are installed. Usually only the newest is needed; remove the older ones if no program depends on them."),
                items = matches.Select(a => new { a.Id, a.Name, a.Version, a.SizeBytes, a.Installed }).OrderBy(a => a.Name).ToList(),
            });
        }
        return groups;
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("tools.menu", _ => MenuItems());
        rpc.On("tools.menuSet", c => SetMenuItem(c.Str("id"), c.Bool("enabled")));
        rpc.On("tools.shred", c => { var paths = c.StrArr("paths"); int passes = Math.Clamp(c.Int("passes", 1), 1, 7); return _jobs.Start("tools.shred", Loc.T("Güvenli silme", "Secure delete"), job => Shred(paths, passes, job)); });
        rpc.On("tools.move", c => Move(c.StrArr("paths"), c.Str("destination")));
        rpc.On("tools.snapshots", _ => Snapshots().Select(s => new { s.Id, s.Time, s.Label, apps = s.Apps.Count, startup = s.Startup.Count, drivers = s.Drivers.Count }).ToList());
        rpc.On("tools.snapshot", c => { string label = c.Str("label", ""); return _jobs.Start("tools.snapshot", Loc.T("Sistem anlık görüntüsü", "System snapshot"), job => Capture(label, job)); });
        rpc.On("tools.snapshotDiff", c => Diff(c.Str("from"), c.Str("to")));
        rpc.On("tools.snapshotDelete", c => { File.Delete(_store.PathOf("snapshots", Path.GetFileName(c.Str("id")) + ".json")); return true; });
        rpc.On("tools.duplicates", _ => Duplicates());
    }
}
