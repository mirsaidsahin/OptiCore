using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Native;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public sealed class Game
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>steam | epic | gog | xbox | custom</summary>
    public string Store { get; set; } = "";
    public string Folder { get; set; } = "";
    public string? Exe { get; set; }
    public long SizeBytes { get; set; }
    public string? LaunchUrl { get; set; }
    public DateTime? LastPlayed { get; set; }
    public KnownGame? Known { get; set; }
    /// <summary>Riot games start through the Riot Client (which also runs the anti-cheat), not their own executable.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? RiotClient { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? RiotProduct { get; set; }
}

public sealed record KnownGame(string Key, LText Note, LText[] Tips);

public sealed class GameProfile
{
    public string GameId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>keep | high | ultimate | a power plan GUID</summary>
    public string PowerPlan { get; set; } = "keep";
    /// <summary>normal | above | high</summary>
    public string Priority { get; set; } = "above";
    /// <summary>Put these background processes into efficiency mode while the game runs.</summary>
    public List<string> Throttle { get; set; } = [];
    public bool ThrottleBrowsers { get; set; } = true;
}

public sealed class FrameStats
{
    public int Frames { get; set; }
    public double AvgFps { get; set; }
    public double Low1 { get; set; }
    public double Low01 { get; set; }
    public double MedianMs { get; set; }
    public double P99Ms { get; set; }
    public double MaxMs { get; set; }
    /// <summary>Frames that took more than twice the median: what you feel as stutter.</summary>
    public int Stutters { get; set; }
    public int[] Histogram { get; set; } = [];
}

public sealed class GameSession
{
    public string Id { get; set; } = "";
    public string GameId { get; set; } = "";
    public string Game { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public double Seconds { get; set; }
    public bool FpsMeasured { get; set; }
    public FrameStats? Frames { get; set; }
    public double AvgCpu { get; set; }
    public double AvgMaxCore { get; set; }
    public double AvgGpu { get; set; }
    public double AvgMem { get; set; }
    public double? MaxTemp { get; set; }
    public double ThrottledSeconds { get; set; }
    /// <summary>gpu | cpu | cpu-thread | memory | thermal | balanced | unknown</summary>
    public string Bottleneck { get; set; } = "unknown";
    public LText? Verdict { get; set; }
    public List<SessionPoint> Timeline { get; set; } = [];
    public List<BackgroundUse> Background { get; set; } = [];
    public bool ProfileApplied { get; set; }
}

public sealed record SessionPoint(double T, double Fps, double Cpu, double MaxCore, double Gpu, double? Temp);
public sealed record BackgroundUse(string Name, double Cpu, long Memory);

/// <summary>
/// Frame timing from ETW: every Present() call of DirectX 9/10/11/12 raises an event with the caller's
/// process id and a high-resolution timestamp. The gap between two presents is the frame time.
/// This is the same principle PresentMon uses; it needs administrator rights (ETW sessions are privileged).
/// </summary>
public sealed class FrameCapture : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private readonly TraceEventSession _session;
    private readonly ConcurrentDictionary<int, (double Last, List<double> Frames)> _byPid = new();
    private readonly Thread _thread;
    public int? TargetPid { get; set; }

    private FrameCapture()
    {
        _session = new TraceEventSession("OptiCore-Frames-" + Environment.ProcessId) { StopOnDispose = true };
        _session.EnableProvider(Dxgi, TraceEventLevel.Informational);
        _session.EnableProvider(D3D9, TraceEventLevel.Informational);
        _session.Source.AllEvents += OnEvent;
        _thread = new Thread(() => { try { _session.Source.Process(); } catch (Exception e) { Log.Warn("frames: trace stopped", e); } }) { IsBackground = true, Name = "OptiCore.Frames" };
        _thread.Start();
    }

    public static FrameCapture? TryStart(out string? error)
    {
        error = null;
        if (!Elevation.IsElevated) { error = "ELEVATION_REQUIRED"; return null; }
        try { return new FrameCapture(); }
        catch (Exception e) { error = e.Message; Log.Warn("frames: cannot start ETW session", e); return null; }
    }

    private void OnEvent(TraceEvent e)
    {
        bool present = (e.ProviderGuid == Dxgi && (int)e.ID == 42) || (e.ProviderGuid == D3D9 && (int)e.ID == 1);
        if (!present) return;
        int pid = e.ProcessID;
        if (TargetPid is int target && pid != target) return;
        double t = e.TimeStampRelativeMSec;
        _byPid.AddOrUpdate(pid, _ => (t, new List<double>(4096)), (_, cur) =>
        {
            double dt = t - cur.Last;
            if (dt > 0.2 && dt < 1000) lock (cur.Frames) cur.Frames.Add(dt);
            return (t, cur.Frames);
        });
    }

    /// <summary>Frame times (ms) recorded for a process since the last call.</summary>
    public List<double> Drain(int pid)
    {
        if (!_byPid.TryGetValue(pid, out var cur)) return [];
        lock (cur.Frames)
        {
            var copy = new List<double>(cur.Frames);
            cur.Frames.Clear();
            return copy;
        }
    }

    /// <summary>The process presenting the most frames: the game when its executable is not known.</summary>
    public int? BusiestPid() => _byPid.OrderByDescending(p => { lock (p.Value.Frames) return p.Value.Frames.Count; }).Select(p => (int?)p.Key).FirstOrDefault();

    public void Dispose()
    {
        try { _session.Dispose(); } catch { /* already stopped */ }
    }

    public static FrameStats Stats(List<double> frameMs)
    {
        if (frameMs.Count < 10) return new FrameStats { Frames = frameMs.Count };
        var sorted = frameMs.OrderBy(x => x).ToList();
        double total = frameMs.Sum();
        double median = sorted[sorted.Count / 2];
        // "1% low" = average FPS over the slowest 1% of frames (the common definition used by reviewers).
        double Low(double fraction)
        {
            int n = Math.Max(1, (int)(sorted.Count * fraction));
            return 1000.0 / sorted.TakeLast(n).Average();
        }
        var hist = new int[12]; // buckets of 4 ms: 0-4, 4-8, ... 44+
        foreach (var f in frameMs) hist[Math.Min(11, (int)(f / 4))]++;
        return new FrameStats
        {
            Frames = frameMs.Count, AvgFps = frameMs.Count * 1000.0 / total, Low1 = Low(0.01), Low01 = Low(0.001),
            MedianMs = median, P99Ms = sorted[(int)(sorted.Count * 0.99)], MaxMs = sorted[^1], Stutters = frameMs.Count(f => f > median * 2 && f > 20), Histogram = hist,
        };
    }
}

public sealed class GamingService : IDisposable
{
    private static readonly string[] SkipSteam = ["228980", "1070560", "1391110", "1628350", "1493710", "250820"]; // redistributables, runtimes, SteamVR
    private static readonly string[] Browsers = ["chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe"];

    private readonly MetricsSampler _metrics;
    private readonly ProcessSampler _processes;
    private readonly ChangeJournal _journal;
    private readonly DataStore _store;
    private readonly NotificationCenter _notices;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private List<Game>? _games;
    private long _gamesAt;
    private Live? _live;
    private FrameCapture? _frames;
    private string? _framesError;

    private sealed class Live
    {
        public GameSession Session = new();
        public Game Game = null!;
        public int Pid;
        public readonly List<double> AllFrames = [];
        public readonly List<double> Recent = [];
        public readonly List<(double Cpu, double MaxCore, double Gpu, double Mem, double? Temp, bool Throttled)> Samples = [];
        public readonly Dictionary<string, (double Cpu, long Mem, int N)> Background = new(StringComparer.OrdinalIgnoreCase);
        public Guid? PreviousPlan;
        public readonly List<int> Throttled = [];
    }

    public GamingService(MetricsSampler metrics, ProcessSampler processes, ChangeJournal journal, DataStore store, NotificationCenter notices)
    {
        _metrics = metrics; _processes = processes; _journal = journal; _store = store; _notices = notices;
        _timer = new Timer(_ => { try { Tick(); } catch (Exception e) { Log.Warn("gaming: tick", e); } }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));

    // ------------------------------------------------------------------ library
    public List<Game> Library(bool refresh = false)
    {
        lock (_gate)
        {
            if (!refresh && _games is not null && Environment.TickCount64 - _gamesAt < 120000) return _games;
            var games = new List<Game>();
            try { Steam(games); } catch (Exception e) { Log.Warn("gaming: steam", e); }
            try { Epic(games); } catch (Exception e) { Log.Warn("gaming: epic", e); }
            try { Gog(games); } catch (Exception e) { Log.Warn("gaming: gog", e); }
            try { Xbox(games); } catch (Exception e) { Log.Warn("gaming: xbox", e); }
            try { Roblox(games); } catch (Exception e) { Log.Warn("gaming: roblox", e); }
            try { Riot(games); } catch (Exception e) { Log.Warn("gaming: riot", e); }
            foreach (var c in _store.Load(Path.Combine("gaming", "custom.json"), () => new List<Game>()).Where(c => Directory.Exists(c.Folder))) games.Add(c);
            var played = Sessions().GroupBy(s => s.GameId).ToDictionary(g => g.Key, g => g.Max(s => s.Start));
            foreach (var g in games)
            {
                g.Known = GameKnowledge.Identify(g);
                if (played.TryGetValue(g.Id, out var last)) g.LastPlayed = last;
                g.Exe ??= GuessExe(g.Folder);
            }
            _games = [.. games.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(g => g.Name)];
            _gamesAt = Environment.TickCount64;
            return _games;
        }
    }

    private static readonly Regex VdfPair = new("\"([^\"]+)\"\\s+\"([^\"]*)\"", RegexOptions.Compiled);

    private static void Steam(List<Game> games)
    {
        string? root = Reg.GetStr(@"HKCU\Software\Valve\Steam", "SteamPath")?.Replace('/', '\\');
        if (root is null || !Directory.Exists(root)) return;
        var libraries = new List<string> { root };
        string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in VdfPair.Matches(File.ReadAllText(vdf)))
                if (m.Groups[1].Value == "path") libraries.Add(m.Groups[2].Value.Replace("\\\\", "\\"));
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var kv = new Dictionary<string, string>();
                foreach (Match m in VdfPair.Matches(File.ReadAllText(acf))) kv.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
                if (!kv.TryGetValue("appid", out var id) || SkipSteam.Contains(id) || !kv.TryGetValue("installdir", out var dir)) continue;
                string folder = Path.Combine(apps, "common", dir);
                if (!Directory.Exists(folder)) continue;
                string name = kv.GetValueOrDefault("name", dir);
                if (name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)) continue;
                games.Add(new Game
                {
                    Id = "steam:" + id, Name = name, Store = "steam", Folder = folder, LaunchUrl = "steam://rungameid/" + id,
                    SizeBytes = long.TryParse(kv.GetValueOrDefault("SizeOnDisk"), out var size) ? size : 0,
                });
            }
        }
    }

    private static void Epic(List<Game> games)
    {
        string dir = Path.Combine(Paths.ProgramData, @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string folder = S("InstallLocation");
            if (!Directory.Exists(folder) || (r.TryGetProperty("bIsApplication", out var app) && app.ValueKind == JsonValueKind.False)) continue;
            games.Add(new Game
            {
                Id = "epic:" + S("AppName"), Name = S("DisplayName"), Store = "epic", Folder = folder,
                Exe = S("LaunchExecutable") is { Length: > 0 } exe ? Path.Combine(folder, exe) : null,
                LaunchUrl = $"com.epicgames.launcher://apps/{Uri.EscapeDataString(S("AppName"))}?action=launch&silent=true",
                SizeBytes = r.TryGetProperty("InstallSize", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0,
            });
        }
    }

    private static void Gog(List<Game> games)
    {
        const string root = @"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games";
        foreach (var id in Reg.SubKeys(root))
        {
            string key = root + "\\" + id;
            string? folder = Reg.GetStr(key, "path");
            if (folder is null || !Directory.Exists(folder)) continue;
            games.Add(new Game { Id = "gog:" + id, Name = Reg.GetStr(key, "gameName") ?? id, Store = "gog", Folder = folder, Exe = Reg.GetStr(key, "exe") });
        }
    }

    private static void Xbox(List<Game> games)
    {
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            string root = Path.Combine(d.Name, "XboxGames");
            if (!Directory.Exists(root)) continue;
            foreach (var folder in Directory.EnumerateDirectories(root))
                if (!Path.GetFileName(folder).Equals("GameSave", StringComparison.OrdinalIgnoreCase))
                    games.Add(new Game { Id = "xbox:" + Path.GetFileName(folder), Name = Path.GetFileName(folder), Store = "xbox", Folder = Path.Combine(folder, "Content") });
        }
    }

    /// <summary>Roblox installs per user into %LOCALAPPDATA%\Roblox\Versions\version-xxxx and changes that folder on every update.</summary>
    private static void Roblox(List<Game> games)
    {
        string versions = Path.Combine(Paths.Local, "Roblox", "Versions");
        if (!Directory.Exists(versions)) return;
        string? exe = Directory.EnumerateDirectories(versions, "version-*")
            .Select(d => Path.Combine(d, "RobloxPlayerBeta.exe")).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (exe is null) return;
        long size = 0;
        try { foreach (var f in new DirectoryInfo(Path.GetDirectoryName(exe)!).EnumerateFiles("*", SearchOption.AllDirectories)) size += f.Length; } catch { /* partial */ }
        // The whole Versions folder is the game folder, so sessions are recognized across updates.
        games.Add(new Game { Id = "roblox:player", Name = "Roblox", Store = "roblox", Folder = versions, Exe = exe, SizeBytes = size });
    }

    /// <summary>Riot games live in "&lt;drive&gt;:\Riot Games\&lt;Game&gt;"; the Riot Client launches them.</summary>
    private static void Riot(List<Game> games)
    {
        var known = new (string Folder, string Id, string Name, string Product)[]
        {
            ("VALORANT", "riot:valorant", "VALORANT", "valorant"),
            ("League of Legends", "riot:lol", "League of Legends", "league_of_legends"),
        };
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            string root = Path.Combine(drive.Name, "Riot Games");
            if (!Directory.Exists(root)) continue;
            string client = Path.Combine(root, "Riot Client", "RiotClientServices.exe");
            foreach (var (folder, id, name, product) in known)
            {
                string path = Path.Combine(root, folder);
                if (!Directory.Exists(path) || games.Any(g => g.Id == id)) continue;
                games.Add(new Game
                {
                    Id = id, Name = name, Store = "riot", Folder = path,
                    LaunchUrl = null, Exe = File.Exists(client) ? null : GuessExe(path),
                    RiotClient = File.Exists(client) ? client : null, RiotProduct = product,
                });
            }
        }
    }

    private static string? GuessExe(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        string[] skip = ["unins", "crash", "setup", "redist", "vcredist", "dxsetup", "launcher", "updater", "uninstall", "report", "helper", "eac", "battleye", "cefprocess", "dotnet", "ue4prereq"];
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
                .Where(f => !skip.Any(s => f.Name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => f.Length).Select(f => f.FullName).FirstOrDefault();
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ session detection
    private Game? Match(ProcInfo p, List<Game> games) =>
        p.Path is null ? null : games.FirstOrDefault(g => p.Path.StartsWith(g.Folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    private void Tick()
    {
        var games = Library();
        if (games.Count == 0 && _live is null) return;

        if (_live is null)
        {
            // A game session starts when a process from a game folder owns the foreground window.
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            Win32.GetWindowThreadProcessId(fg, out uint pid);
            var proc = _processes.Snapshot(1500).FirstOrDefault(p => p.Pid == pid);
            if (proc is null) return;
            var game = Match(proc, games);
            if (game is null) return;
            Begin(game, proc);
            return;
        }

        var live = _live;
        var snapshot = _processes.Snapshot(1500);
        if (!snapshot.Any(p => p.Pid == live.Pid)) { End(); return; }
        _metrics.Touch();
        var s = _metrics.Latest;
        double elapsed = (DateTime.UtcNow - live.Session.Start).TotalSeconds;

        var frames = _frames?.Drain(live.Pid) ?? [];
        live.AllFrames.AddRange(frames);
        live.Recent.AddRange(frames);
        if (live.Recent.Count > 600) live.Recent.RemoveRange(0, live.Recent.Count - 600);
        double fps = frames.Count > 0 ? frames.Count * 1000.0 / Math.Max(1, frames.Sum()) : 0;

        if (s is not null)
        {
            double maxCore = s.Cores.Length > 0 ? s.Cores.Max() : s.Cpu;
            live.Samples.Add((s.Cpu, maxCore, s.Gpu, s.MemPercent, s.Temp, s.Throttled));
            if (live.Session.Timeline.Count < 5400) live.Session.Timeline.Add(new SessionPoint(Math.Round(elapsed, 1), Math.Round(fps, 1), s.Cpu, maxCore, s.Gpu, s.Temp));
        }
        foreach (var p in snapshot.Where(p => p.Pid != live.Pid && p.Pid > 4 && p.Cpu > 0.3))
        {
            string name = p.Description ?? p.Name;
            var cur = live.Background.GetValueOrDefault(name);
            live.Background[name] = (cur.Cpu + p.Cpu, Math.Max(cur.Mem, p.Memory), cur.N + 1);
        }
    }

    private void Begin(Game game, ProcInfo proc)
    {
        var live = new Live { Game = game, Pid = proc.Pid };
        live.Session = new GameSession { Id = DateTime.Now.ToString("yyyyMMdd-HHmmss"), GameId = game.Id, Game = game.Name, Start = DateTime.UtcNow };
        _frames ??= FrameCapture.TryStart(out _framesError);
        if (_frames is not null) _frames.TargetPid = proc.Pid;
        live.Session.FpsMeasured = _frames is not null;

        var profile = Profiles().FirstOrDefault(p => p.GameId == game.Id);
        if (profile is { Enabled: true } && !_journal.DryRun) ApplyProfile(live, profile, proc);
        _live = live;
        Log.Info($"gaming: session started {game.Name} pid={proc.Pid} fps={live.Session.FpsMeasured}");
    }

    private void ApplyProfile(Live live, GameProfile profile, ProcInfo proc)
    {
        try
        {
            if (profile.Priority is "above" or "high") ProcessService.SetPriority(proc.Pid, profile.Priority);
            Guid? plan = profile.PowerPlan switch
            {
                "keep" => null,
                "high" => PowerPlans.List().FirstOrDefault(p => p.Kind == "high")?.Id,
                "ultimate" => PowerPlans.List().FirstOrDefault(p => p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Nihai", StringComparison.OrdinalIgnoreCase))?.Id,
                var g when Guid.TryParse(g, out var guid) => guid,
                _ => null,
            };
            if (plan is Guid target && PowerPlans.Active() is Guid current && current != target)
            {
                live.PreviousPlan = current;
                PowerPlans.SetActive(target);
            }
            var names = new HashSet<string>(profile.Throttle, StringComparer.OrdinalIgnoreCase);
            if (profile.ThrottleBrowsers) foreach (var b in Browsers) names.Add(b);
            foreach (var p in _processes.Snapshot(0).Where(p => names.Contains(p.Name) && !p.Protected))
            {
                try { ProcessService.SetEco(p.Pid, true); live.Throttled.Add(p.Pid); } catch { /* exited or protected */ }
            }
            live.Session.ProfileApplied = true;
        }
        catch (Exception e) { Log.Warn("gaming: profile", e); }
    }

    private void RevertProfile(Live live)
    {
        if (live.PreviousPlan is Guid plan) PowerPlans.SetActive(plan);
        foreach (int pid in live.Throttled) { try { ProcessService.SetEco(pid, false); } catch { /* gone */ } }
    }

    private void End()
    {
        var live = _live;
        if (live is null) return;
        _live = null;
        RevertProfile(live);
        var s = live.Session;
        s.End = DateTime.UtcNow;
        s.Seconds = (s.End.Value - s.Start).TotalSeconds;
        if (s.Seconds < 30) return; // alt-tabs and launchers are not sessions
        if (live.AllFrames.Count >= 10) s.Frames = FrameCapture.Stats(live.AllFrames);
        if (live.Samples.Count > 0)
        {
            s.AvgCpu = live.Samples.Average(x => x.Cpu);
            s.AvgMaxCore = live.Samples.Average(x => x.MaxCore);
            s.AvgGpu = live.Samples.Average(x => x.Gpu);
            s.AvgMem = live.Samples.Average(x => x.Mem);
            s.MaxTemp = live.Samples.Where(x => x.Temp is not null).Select(x => x.Temp).Max();
            s.ThrottledSeconds = live.Samples.Count(x => x.Throttled) * 2;
        }
        (s.Bottleneck, s.Verdict) = Analyze(s, live.Samples.Count);
        s.Background = [.. live.Background.Select(b => new BackgroundUse(b.Key, b.Value.Cpu / Math.Max(1, live.Samples.Count), b.Value.Mem)).Where(b => b.Cpu >= 0.5).OrderByDescending(b => b.Cpu).Take(10)];
        if (s.Timeline.Count > 900) s.Timeline = [.. s.Timeline.Where((_, i) => i % (s.Timeline.Count / 900 + 1) == 0)];
        _store.Save(Path.Combine("gaming", "sessions", s.Id + ".json"), s);
        _notices.Add(NoticeLevel.Info, Loc.T("Oyun oturumu kaydedildi: {0}", "Game session saved: {0}").F(s.Game),
            s.Frames is { } f ? Loc.T("Ortalama {0:0} FPS, %1 düşük {1:0} FPS. {2}", "Average {0:0} FPS, 1% low {1:0} FPS. {2}").F(f.AvgFps, f.Low1, s.Verdict ?? "") : s.Verdict ?? Loc.T("Oturum özeti hazır.", "The session summary is ready."),
            "gaming/sessions");
        Log.Info($"gaming: session ended {s.Game} {s.Seconds:0}s");
    }

    /// <summary>What limited the frame rate. The rules mirror how reviewers read a CPU/GPU utilization overlay.</summary>
    internal static (string, LText?) Analyze(GameSession s, int samples)
    {
        if (samples < 5) return ("unknown", null);
        if (s.ThrottledSeconds > s.Seconds * 0.2)
            return ("thermal", Loc.T("Isıl kısılma: oturumun önemli bölümünde işlemci ısı yüzünden yavaşlatıldı. Soğutmayı iyileştirmek FPS'i doğrudan artırır.", "Thermal throttling: for a large part of the session the CPU was slowed down by heat. Better cooling directly raises FPS."));
        if (s.AvgMem > 90)
            return ("memory", Loc.T("Bellek yetersiz: sistem diske sayfaladı; takılmaların ana nedeni bu olabilir. Arka plandaki uygulamaları kapatın ya da bellek ekleyin.", "Not enough memory: the system paged to disk, likely the main cause of stutter. Close background apps or add memory."));
        if (s.AvgGpu >= 90)
            return ("gpu", Loc.T("Ekran kartı sınırında: FPS'i ekran kartı belirliyor. Çözünürlük, ölçekleme, gölge ve kenar yumuşatma ayarlarını düşürmek en çok kazandırır; işlemci ayarları fark yaratmaz.", "GPU bound: the graphics card sets the frame rate. Lowering resolution, scaling, shadows and anti-aliasing gains the most; CPU-side settings will not help."));
        if (s.AvgMaxCore >= 85 && s.AvgGpu < 75)
            return (s.AvgCpu < 50 ? "cpu-thread" : "cpu", s.AvgCpu < 50
                ? Loc.T("Tek çekirdek sınırında: oyunun ana iş parçacığı bir çekirdeği doldurmuş, ekran kartı ise boşta bekliyor. Görüş mesafesi, trafik ve NPC yoğunluğu gibi işlemci ayarlarını düşürün; grafik ayarlarını yükseltmek FPS'i düşürmez.",
                        "Single-thread bound: the game's main thread filled one core while the graphics card waits. Lower CPU-side settings such as view distance, traffic and NPC density; raising graphics settings will not cost FPS.")
                : Loc.T("İşlemci sınırında: ekran kartı tam kullanılmıyor. Arka plan uygulamalarını kapatın ve işlemci yoğun ayarları düşürün.", "CPU bound: the graphics card is not fully used. Close background apps and lower CPU-heavy settings."));
        if (s.Frames is { } f && f.AvgFps > 0 && Math.Abs(f.AvgFps - Math.Round(f.AvgFps / 30) * 30) < 2 && s.AvgGpu < 80)
            return ("capped", Loc.T("Kare hızı sınırlı (V-Sync ya da FPS sınırı): ne işlemci ne ekran kartı tam yükte. Daha akıcı görüntü için sınırı yükseltebilirsiniz.", "The frame rate is capped (V-Sync or an FPS limit): neither CPU nor GPU is fully loaded. You can raise the cap for smoother motion."));
        return ("balanced", Loc.T("Dengeli: belirgin bir darboğaz yok.", "Balanced: no clear bottleneck."));
    }

    // ------------------------------------------------------------------ storage
    private List<GameSession> Sessions()
    {
        var list = new List<GameSession>();
        string dir = _store.DirOf("gaming", "sessions");
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderByDescending(f => f).Take(300))
        {
            var s = _store.Load<GameSession?>(Path.Combine("gaming", "sessions", Path.GetFileName(file)), () => null);
            if (s is not null) list.Add(s);
        }
        return list;
    }

    private List<GameProfile> Profiles() => _store.Load(Path.Combine("gaming", "profiles.json"), () => new List<GameProfile>());

    /// <summary>Compact state for the overlay window.</summary>
    public (bool Active, double? Fps, FrameStats? Stats) OverlayInfo()
    {
        var live = _live;
        if (live is null) return (false, null, null);
        var recent = live.Recent.TakeLast(120).ToList();
        double? fps = live.Session.FpsMeasured && recent.Count > 5 ? 1000.0 / recent.Average() : null;
        return (true, fps, live.AllFrames.Count >= 10 ? FrameCapture.Stats(live.AllFrames.TakeLast(3000).ToList()) : null);
    }

    public object LiveView()
    {
        var live = _live;
        if (live is null) return new { active = false, fpsAvailable = _frames is not null || Elevation.IsElevated, framesError = _framesError };
        var recent = live.Recent.TakeLast(240).ToList();
        var last = live.Session.Timeline.LastOrDefault();
        var stats = live.AllFrames.Count >= 10 ? FrameCapture.Stats(live.AllFrames) : null;
        var (bottleneck, verdict) = Analyze(new GameSession
        {
            Seconds = (DateTime.UtcNow - live.Session.Start).TotalSeconds, Frames = stats,
            AvgCpu = live.Samples.TakeLast(15).DefaultIfEmpty().Average(x => x.Cpu), AvgMaxCore = live.Samples.TakeLast(15).DefaultIfEmpty().Average(x => x.MaxCore),
            AvgGpu = live.Samples.TakeLast(15).DefaultIfEmpty().Average(x => x.Gpu), AvgMem = live.Samples.TakeLast(15).DefaultIfEmpty().Average(x => x.Mem),
            ThrottledSeconds = live.Samples.TakeLast(15).Count(x => x.Throttled) * 2,
        }, Math.Min(15, live.Samples.Count));
        return new
        {
            active = true, game = live.Game.Name, gameId = live.Game.Id, pid = live.Pid, start = live.Session.Start, fpsMeasured = live.Session.FpsMeasured,
            fps = last?.Fps, frametimes = recent, stats, bottleneck, verdict, profileApplied = live.Session.ProfileApplied, framesError = _framesError,
            timeline = live.Session.Timeline.TakeLast(120).ToList(),
        };
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("gaming.library", c => Library(c.Bool("refresh")));
        rpc.On("gaming.live", _ => LiveView());
        rpc.On("gaming.sessions", c =>
        {
            string? game = c.StrOrNull("game");
            return Sessions().Where(s => game is null || s.GameId == game).Select(s => new
            {
                s.Id, s.GameId, s.Game, s.Start, s.Seconds, s.FpsMeasured, avgFps = s.Frames?.AvgFps, low1 = s.Frames?.Low1, s.AvgCpu, s.AvgGpu, s.MaxTemp, s.Bottleneck, s.ProfileApplied,
            }).ToList();
        });
        rpc.On("gaming.session", c => _store.Load<GameSession?>(Path.Combine("gaming", "sessions", Path.GetFileName(c.Str("id")) + ".json"), () => null) ?? throw RpcException.NotFound("Session not found"));
        rpc.On("gaming.deleteSession", c => { File.Delete(_store.PathOf("gaming", "sessions", Path.GetFileName(c.Str("id")) + ".json")); return true; });
        rpc.On("gaming.profiles", _ => Profiles());
        rpc.On("gaming.saveProfile", c =>
        {
            var p = c.Get<GameProfile>("profile");
            if (p.Priority is not ("normal" or "above" or "high")) throw RpcException.Invalid("priority");
            var all = Profiles();
            all.RemoveAll(x => x.GameId == p.GameId);
            all.Add(p);
            _store.Save(Path.Combine("gaming", "profiles.json"), all);
            return all;
        });
        rpc.On("gaming.addCustom", c =>
        {
            string exe = c.Str("exe");
            if (!File.Exists(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw RpcException.NotFound(Loc.S("Oyun dosyası bulunamadı.", "Game executable not found."));
            var all = _store.Load(Path.Combine("gaming", "custom.json"), () => new List<Game>());
            string folder = Path.GetDirectoryName(exe)!;
            all.RemoveAll(g => g.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
            all.Add(new Game { Id = "custom:" + Guid.NewGuid().ToString("N")[..8], Name = c.Str("name", Path.GetFileNameWithoutExtension(exe)), Store = "custom", Folder = folder, Exe = exe });
            _store.Save(Path.Combine("gaming", "custom.json"), all);
            return Library(true);
        });
        rpc.On("gaming.launch", c =>
        {
            var g = Library().FirstOrDefault(x => x.Id == c.Str("id")) ?? throw RpcException.NotFound("Game not found");
            if (g.RiotClient is not null) Process.Start(new ProcessStartInfo(g.RiotClient, $"--launch-product={g.RiotProduct} --launch-patchline=live") { UseShellExecute = true });
            else if (g.LaunchUrl is not null) Process.Start(new ProcessStartInfo(g.LaunchUrl) { UseShellExecute = true });
            else if (g.Exe is not null) Process.Start(new ProcessStartInfo(g.Exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(g.Exe)! });
            else throw RpcException.Unsupported("No way to launch this game");
            return true;
        });
        rpc.On("gaming.endSession", _ => { End(); return true; });
    }

    public void Dispose()
    {
        _timer.Dispose();
        if (_live is not null) RevertProfile(_live);
        _frames?.Dispose();
    }
}
