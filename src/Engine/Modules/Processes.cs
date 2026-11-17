using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Native;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public sealed class ProcInfo
{
    public int Pid { get; set; }
    public int ParentPid { get; set; }
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public string? Description { get; set; }
    public string? Company { get; set; }
    /// <summary>Percent of total CPU capacity, like Task Manager.</summary>
    public double Cpu { get; set; }
    /// <summary>Private working set: the memory only this process holds.</summary>
    public long Memory { get; set; }
    public long WorkingSet { get; set; }
    public long Commit { get; set; }
    public double IoRead { get; set; }
    public double IoWrite { get; set; }
    public double Gpu { get; set; }
    public int Threads { get; set; }
    public int Handles { get; set; }
    public long Started { get; set; }
    public int Session { get; set; }
    public int Priority { get; set; }
    /// <summary>app (has a window) | background | windows</summary>
    public string Kind { get; set; } = "background";
    public bool Protected { get; set; }
    public string[]? Services { get; set; }
    public long CpuTimeMs { get; set; }
}

/// <summary>Reads the whole process table with a single NtQuerySystemInformation call and derives rates between samples.</summary>
public sealed class ProcessSampler(MetricsSampler metrics)
{
    private sealed record Prev(long Create, long CpuTime, long IoRead, long IoWrite);
    private sealed record FileMeta(string? Description, string? Company);

    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Secure System", "Memory Compression", "System Idle Process", "smss.exe", "csrss.exe", "wininit.exe",
        "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe", "fontdrvhost.exe", "MsMpEng.exe", "NisSrv.exe", "MpDefenderCoreService.exe",
    };

    private readonly object _gate = new();
    private Dictionary<int, Prev> _prev = [];
    private long _prevTicks;
    private List<ProcInfo> _last = [];
    private long _lastAt;
    private int _bufferSize = 1 << 20;
    private readonly Dictionary<(int Pid, long Create), string?> _paths = [];
    private static readonly ConcurrentDictionary<string, FileMeta> Meta = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string?> SystemBinary = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<int, string[]> _services = [];
    private long _servicesAt;
    private readonly string _windowsDir = Paths.Windows;

    public static bool IsCritical(string name) => Critical.Contains(name);

    public List<ProcInfo> Snapshot(int maxAgeMs = 1500)
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            if (_last.Count > 0 && now - _lastAt < maxAgeMs) return _last;
            try { _last = Sample(); _lastAt = now; }
            catch (Exception e) { Log.Warn("processes: sample failed", e); }
            return _last;
        }
    }

    private List<ProcInfo> Sample()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            while (true)
            {
                buffer = Marshal.AllocHGlobal(_bufferSize);
                int status = Win32.NtQuerySystemInformation(Win32.SystemProcessInformation, buffer, _bufferSize, out int needed);
                if (status == 0) break;
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                if (status != Win32.STATUS_INFO_LENGTH_MISMATCH) throw new Win32Exception($"NtQuerySystemInformation 0x{status:X8}");
                _bufferSize = Math.Max(needed, _bufferSize) + (256 << 10);
            }
            return Parse(buffer);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private List<ProcInfo> Parse(IntPtr buffer)
    {
        long ticks = Stopwatch.GetTimestamp();
        double dt = _prevTicks == 0 ? 0 : (ticks - _prevTicks) / (double)Stopwatch.Frequency;
        _prevTicks = ticks;
        int cores = Environment.ProcessorCount;
        var gpu = metrics.GpuByPid;
        var windows = WindowOwners();
        RefreshServices();
        var services = _services;

        var list = new List<ProcInfo>(400);
        var next = new Dictionary<int, Prev>(400);
        IntPtr p = buffer;
        while (true)
        {
            // SYSTEM_PROCESS_INFORMATION (x64)
            int nextOffset = Marshal.ReadInt32(p, 0x00);
            int threads = Marshal.ReadInt32(p, 0x04);
            long privateWs = Marshal.ReadInt64(p, 0x08);
            long create = Marshal.ReadInt64(p, 0x20);
            long cpuTime = Marshal.ReadInt64(p, 0x28) + Marshal.ReadInt64(p, 0x30);
            int nameBytes = (ushort)Marshal.ReadInt16(p, 0x38);
            IntPtr namePtr = Marshal.ReadIntPtr(p, 0x40);
            int basePriority = Marshal.ReadInt32(p, 0x48);
            int pid = (int)Marshal.ReadIntPtr(p, 0x50);
            int parent = (int)Marshal.ReadIntPtr(p, 0x58);
            int handles = Marshal.ReadInt32(p, 0x60);
            int session = Marshal.ReadInt32(p, 0x64);
            long workingSet = Marshal.ReadInt64(p, 0x90);
            long commit = Marshal.ReadInt64(p, 0xC8);
            long ioRead = Marshal.ReadInt64(p, 0xE8);
            long ioWrite = Marshal.ReadInt64(p, 0xF0);

            if (pid != 0)
            {
                string name = nameBytes > 0 && namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr, nameBytes / 2) : "System";
                var info = new ProcInfo
                {
                    Pid = pid, ParentPid = parent, Name = name, Threads = threads, Handles = handles, Session = session, Priority = basePriority,
                    Memory = privateWs, WorkingSet = workingSet, Commit = commit, CpuTimeMs = cpuTime / 10000,
                    Started = create > 0 ? new DateTimeOffset(DateTime.FromFileTimeUtc(create)).ToUnixTimeMilliseconds() : 0,
                    Protected = Critical.Contains(name) || pid == 4,
                };
                if (dt > 0 && _prev.TryGetValue(pid, out var prev) && prev.Create == create)
                {
                    info.Cpu = Math.Clamp((cpuTime - prev.CpuTime) / (dt * 1e7 * cores) * 100.0, 0, 100);
                    info.IoRead = Math.Max(0, (ioRead - prev.IoRead) / dt);
                    info.IoWrite = Math.Max(0, (ioWrite - prev.IoWrite) / dt);
                }
                next[pid] = new Prev(create, cpuTime, ioRead, ioWrite);

                if (pid > 4)
                {
                    // Protected system processes refuse to tell their path; they live in System32, so look there by name.
                    info.Path = ResolvePath(pid, create) ?? SystemBinary.GetOrAdd(name, static n =>
                    {
                        if (n.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return null;
                        var candidate = System.IO.Path.Combine(Environment.SystemDirectory, n);
                        return File.Exists(candidate) ? candidate : null;
                    });
                    if (info.Path is not null)
                    {
                        var meta = Meta.GetOrAdd(info.Path, static path =>
                        {
                            try { var v = FileVersionInfo.GetVersionInfo(path); return new FileMeta(Clean(v.FileDescription), Clean(v.CompanyName)); }
                            catch { return new FileMeta(null, null); }
                        });
                        info.Description = meta.Description;
                        info.Company = meta.Company;
                    }
                }
                if (gpu.TryGetValue(pid, out var g)) info.Gpu = g;
                if (services.TryGetValue(pid, out var svc)) info.Services = svc;
                info.Kind = windows.Contains(pid) ? "app"
                    : session == 0 || (info.Path?.StartsWith(_windowsDir, StringComparison.OrdinalIgnoreCase) ?? pid <= 4 || info.Protected) ? "windows"
                    : "background";
                list.Add(info);
            }
            if (nextOffset == 0) break;
            p += nextOffset;
        }
        _prev = next;
        if (_paths.Count > 4000)
            foreach (var key in _paths.Keys.Where(k => !next.TryGetValue(k.Pid, out var live) || live.Create != k.Create).ToList()) _paths.Remove(key);
        return list;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private string? ResolvePath(int pid, long create)
    {
        if (_paths.TryGetValue((pid, create), out var cached)) return cached;
        string? path = QueryPath(pid);
        _paths[(pid, create)] = path;
        return path;
    }

    public static string? QueryPath(int pid)
    {
        IntPtr h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            return Win32.QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally { Win32.CloseHandle(h); }
    }

    /// <summary>Processes that own a real, visible top-level window: what Task Manager lists under "Apps".</summary>
    private static HashSet<int> WindowOwners()
    {
        var owners = new HashSet<int>();
        try
        {
            Win32.EnumWindows((hwnd, _) =>
            {
                if (Win32.IsWindowVisible(hwnd) && Win32.GetWindowTextLengthW(hwnd) > 0 && Win32.GetWindow(hwnd, 4) == IntPtr.Zero
                    && (Win32.GetWindowLongW(hwnd, -20) & 0x80) == 0)
                {
                    Win32.GetWindowThreadProcessId(hwnd, out uint pid);
                    owners.Add((int)pid);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { /* desktop not available (service session) */ }
        return owners;
    }

    private void RefreshServices()
    {
        long now = Environment.TickCount64;
        if (_servicesAt != 0 && now - _servicesAt < 60000) return;
        _servicesAt = now;
        Task.Run(() =>
        {
            var map = new Dictionary<int, List<string>>();
            foreach (var row in Wmi.Query("SELECT Name, ProcessId FROM Win32_Service WHERE State = 'Running'"))
            {
                int pid = row.I("ProcessId");
                if (pid <= 0) continue;
                if (!map.TryGetValue(pid, out var names)) map[pid] = names = [];
                names.Add(row.S("Name"));
            }
            _services = map.ToDictionary(m => m.Key, m => m.Value.ToArray());
        });
    }
}

public sealed record Connection(int Pid, string Protocol, string LocalAddress, int LocalPort, string? RemoteAddress, int RemotePort, string State);

/// <summary>Every TCP connection and UDP listener with its owning process (no admin rights needed).</summary>
public static class ConnectionTable
{
    private static readonly string[] States = ["", "closed", "listen", "syn-sent", "syn-received", "established", "fin-wait-1", "fin-wait-2", "close-wait", "closing", "last-ack", "time-wait", "delete"];

    public static List<Connection> Snapshot()
    {
        var list = new List<Connection>(256);
        Read(true, Win32.AF_INET, 24, (row, l) =>
        {
            int state = Marshal.ReadInt32(row, 0);
            l.Add(new Connection(Marshal.ReadInt32(row, 20), "TCP", V4(row, 4), Port(row, 8), V4(row, 12), Port(row, 16), StateName(state)));
        }, list);
        Read(true, Win32.AF_INET6, 56, (row, l) =>
        {
            int state = Marshal.ReadInt32(row, 48);
            l.Add(new Connection(Marshal.ReadInt32(row, 52), "TCP6", V6(row, 0), Port(row, 20), V6(row, 24), Port(row, 44), StateName(state)));
        }, list);
        Read(false, Win32.AF_INET, 12, (row, l) => l.Add(new Connection(Marshal.ReadInt32(row, 8), "UDP", V4(row, 0), Port(row, 4), null, 0, "listen")), list);
        Read(false, Win32.AF_INET6, 28, (row, l) => l.Add(new Connection(Marshal.ReadInt32(row, 24), "UDP6", V6(row, 0), Port(row, 20), null, 0, "listen")), list);
        return list;
    }

    private static string StateName(int state) => state > 0 && state < States.Length ? States[state] : "unknown";
    private static string V4(IntPtr row, int offset) => new IPAddress((long)(uint)Marshal.ReadInt32(row, offset)).ToString();
    private static int Port(IntPtr row, int offset) { int raw = Marshal.ReadInt32(row, offset); return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); }

    private static string V6(IntPtr row, int offset)
    {
        var bytes = new byte[16];
        Marshal.Copy(row + offset, bytes, 0, 16);
        return new IPAddress(bytes).ToString();
    }

    private static void Read(bool tcp, int family, int rowSize, Action<IntPtr, List<Connection>> parse, List<Connection> list)
    {
        int size = 0;
        if (tcp) Win32.GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, Win32.TCP_TABLE_OWNER_PID_ALL, 0);
        else Win32.GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, Win32.UDP_TABLE_OWNER_PID, 0);
        if (size <= 0) return;
        size += 8192;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint rc = tcp
                ? Win32.GetExtendedTcpTable(buffer, ref size, false, family, Win32.TCP_TABLE_OWNER_PID_ALL, 0)
                : Win32.GetExtendedUdpTable(buffer, ref size, false, family, Win32.UDP_TABLE_OWNER_PID, 0);
            if (rc != 0) return;
            int count = Marshal.ReadInt32(buffer);
            for (int i = 0; i < count; i++) parse(buffer + 4 + i * rowSize, list);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

public sealed class ProcessRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>Executable name, for example "chrome.exe".</summary>
    public string Match { get; set; } = "";
    /// <summary>idle | below | normal | above | high, or empty to leave the priority alone.</summary>
    public string Priority { get; set; } = "";
    public bool Eco { get; set; }
    public bool Enabled { get; set; } = true;
    public int Hits { get; set; }
}

public sealed class ProcessService
{
    private sealed class Track
    {
        public readonly Queue<(long T, double Cpu, long Mem)> Points = new();
        public int HighCpuTicks;
        public long FirstMem;
        public long FirstAt;
        public bool LeakReported;
    }

    private readonly ProcessSampler _sampler;
    private readonly ChangeJournal _journal;
    private readonly DataStore _store;
    private readonly SettingsService _settings;
    private readonly NotificationCenter _notices;
    private readonly Timer _monitor;
    private readonly object _gate = new();
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(int Pid, long Started)> _ruleApplied = [];
    private readonly Dictionary<string, (double CpuSeconds, long MemSum, int Samples)> _daily = new(StringComparer.OrdinalIgnoreCase);
    private List<ProcessRule> _rules;
    private int _monitorTick;
    private string _dailyKey = DateTime.Now.ToString("yyyyMMdd");

    private const int MonitorSeconds = 5;

    public ProcessService(ProcessSampler sampler, ChangeJournal journal, DataStore store, SettingsService settings, NotificationCenter notices)
    {
        _sampler = sampler; _journal = journal; _store = store; _settings = settings; _notices = notices;
        _rules = store.Load(Path.Combine("processes", "rules.json"), () => new List<ProcessRule>());
        foreach (var (name, value) in store.Load(DailyFile(_dailyKey), () => new Dictionary<string, double[]>()))
            if (value.Length == 3) _daily[name] = (value[0], (long)value[1], (int)value[2]);
        _monitor = new Timer(_ => { try { Monitor(); } catch (Exception e) { Log.Warn("processes: monitor failed", e); } }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _monitor.Change(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(MonitorSeconds));

    private static string DailyFile(string key) => Path.Combine("history", "apps", $"apps-{key}.json");
    private static string GroupOf(ProcInfo p) => p.Description ?? (p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.Name[..^4] : p.Name);

    // ------------------------------------------------------------------ background monitor
    private void Monitor()
    {
        var snapshot = _sampler.Snapshot(MonitorSeconds * 1000 - 500);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var alerts = _settings.Current.Alerts;
        _monitorTick++;

        lock (_gate)
        {
            foreach (var group in snapshot.Where(p => p.Pid > 4).GroupBy(GroupOf))
            {
                double cpu = group.Sum(p => p.Cpu);
                long mem = group.Sum(p => p.Memory);

                var day = _daily.GetValueOrDefault(group.Key);
                _daily[group.Key] = (day.CpuSeconds + cpu / 100.0 * Environment.ProcessorCount * MonitorSeconds, day.MemSum + mem / (1 << 20), day.Samples + 1);

                if (cpu < 1 && mem < 150L << 20 && !_tracks.ContainsKey(group.Key)) continue;
                if (!_tracks.TryGetValue(group.Key, out var track)) _tracks[group.Key] = track = new Track { FirstMem = mem, FirstAt = now };
                track.Points.Enqueue((now, cpu, mem));
                while (track.Points.Count > 120) track.Points.Dequeue();

                if (!alerts.Enabled) continue;
                track.HighCpuTicks = cpu >= 40 ? track.HighCpuTicks + 1 : 0;
                if (track.HighCpuTicks * MonitorSeconds == Math.Max(60, alerts.CpuSeconds) && !ShellOps.IsFullscreenBusy())
                    _notices.Add(NoticeLevel.Warn,
                        Loc.T("{0} uzun süredir işlemciyi yoruyor", "{0} has been heavy on the CPU").F(group.Key),
                        Loc.T("Son {0} saniyedir ortalama %{1:0} CPU kullanıyor.", "It has averaged {1:0}% CPU for the last {0} seconds.").F(track.HighCpuTicks * MonitorSeconds, cpu),
                        "processes", "cpu-hog:" + group.Key, coolDownHours: 2);

                // Leak heuristic: memory that keeps climbing for 15+ minutes and has grown by 400 MB and 60%.
                if (alerts.LeakDetection && !track.LeakReported && now - track.FirstAt > 15 * 60000 && track.Points.Count >= 100)
                {
                    long growth = mem - track.FirstMem;
                    var pts = track.Points.ToArray();
                    bool rising = pts[^1].Mem > pts[pts.Length / 2].Mem && pts[pts.Length / 2].Mem > pts[0].Mem;
                    if (rising && growth > 400L << 20 && growth > track.FirstMem * 0.6)
                    {
                        track.LeakReported = true;
                        _notices.Add(NoticeLevel.Warn,
                            Loc.T("{0} bellek sızdırıyor olabilir", "{0} may be leaking memory").F(group.Key),
                            Loc.T("Belleği {0} artarak {1} oldu ve yükselmeye devam ediyor. Uygulamayı yeniden başlatmak belleği geri kazandırır.",
                                  "Its memory grew by {0} to {1} and is still climbing. Restarting the app reclaims it.").F(new ByteSize(growth), new ByteSize(mem)),
                            "processes", "leak:" + group.Key, coolDownHours: 6);
                    }
                }
            }
            foreach (var stale in _tracks.Where(t => t.Value.Points.Count > 0 && now - t.Value.Points.Last().T > 120000).Select(t => t.Key).ToList()) _tracks.Remove(stale);

            if (_monitorTick % 60 == 0) FlushDaily();
        }
        ApplyRules(snapshot);
    }

    private void FlushDaily()
    {
        string key = DateTime.Now.ToString("yyyyMMdd");
        _store.Save(DailyFile(_dailyKey), _daily.Where(d => d.Value.CpuSeconds > 1 || d.Value.MemSum / Math.Max(1, d.Value.Samples) > 200)
            .ToDictionary(d => d.Key, d => new[] { d.Value.CpuSeconds, d.Value.MemSum, d.Value.Samples }));
        if (key != _dailyKey) { _dailyKey = key; _daily.Clear(); }
    }

    private void ApplyRules(List<ProcInfo> snapshot)
    {
        List<ProcessRule> rules;
        lock (_gate) rules = [.. _rules.Where(r => r.Enabled && r.Match.Length > 0)];
        if (rules.Count == 0 || _journal.DryRun) return;
        bool changed = false;
        foreach (var p in snapshot)
        {
            var rule = rules.FirstOrDefault(r => string.Equals(r.Match, p.Name, StringComparison.OrdinalIgnoreCase));
            if (rule is null || p.Protected || !_ruleApplied.Add((p.Pid, p.Started))) continue;
            try
            {
                if (rule.Eco) SetEco(p.Pid, true);
                else if (rule.Priority.Length > 0) SetPriority(p.Pid, rule.Priority);
                rule.Hits++;
                changed = true;
            }
            catch { /* the process may have exited or be out of reach */ }
        }
        if (_ruleApplied.Count > 5000) _ruleApplied.Clear();
        if (changed) lock (_gate) _store.Save(Path.Combine("processes", "rules.json"), _rules);
    }

    // ------------------------------------------------------------------ actions
    private static uint PriorityClass(string level) => level switch
    {
        "idle" => Win32.IDLE_PRIORITY_CLASS,
        "below" => Win32.BELOW_NORMAL_PRIORITY_CLASS,
        "normal" => Win32.NORMAL_PRIORITY_CLASS,
        "above" => Win32.ABOVE_NORMAL_PRIORITY_CLASS,
        "high" => Win32.HIGH_PRIORITY_CLASS,
        _ => throw RpcException.Invalid("Unknown priority level. Realtime is intentionally not offered: it can freeze the system."),
    };

    private static string PriorityName(uint cls) => cls switch
    {
        Win32.IDLE_PRIORITY_CLASS => "idle",
        Win32.BELOW_NORMAL_PRIORITY_CLASS => "below",
        Win32.NORMAL_PRIORITY_CLASS => "normal",
        Win32.ABOVE_NORMAL_PRIORITY_CLASS => "above",
        Win32.HIGH_PRIORITY_CLASS => "high",
        Win32.REALTIME_PRIORITY_CLASS => "realtime",
        _ => "unknown",
    };

    private static T WithHandle<T>(int pid, uint access, Func<IntPtr, T> use)
    {
        IntPtr h = Win32.OpenProcess(access, false, pid);
        if (h == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw err == 5 ? new UnauthorizedAccessException("OpenProcess") : RpcException.NotFound(Loc.S("İşlem artık çalışmıyor.", "The process is no longer running."));
        }
        try { return use(h); }
        finally { Win32.CloseHandle(h); }
    }

    internal static void SetPriority(int pid, string level) => WithHandle(pid, Win32.PROCESS_SET_INFORMATION, h =>
    {
        if (!Win32.SetPriorityClass(h, PriorityClass(level))) throw new Win32Exception(Marshal.GetLastWin32Error());
        return true;
    });

    /// <summary>Windows 11 "Efficiency mode": EcoQoS execution speed throttling plus idle priority.</summary>
    internal static void SetEco(int pid, bool on) => WithHandle(pid, Win32.PROCESS_SET_INFORMATION, h =>
    {
        var state = new Win32.PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1,
            ControlMask = Win32.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = on ? Win32.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
        };
        if (!Win32.SetProcessInformation(h, Win32.ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<Win32.PROCESS_POWER_THROTTLING_STATE>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Win32.SetPriorityClass(h, on ? Win32.IDLE_PRIORITY_CLASS : Win32.NORMAL_PRIORITY_CLASS);
        return true;
    });

    private ProcInfo Target(int pid)
    {
        var p = _sampler.Snapshot().FirstOrDefault(x => x.Pid == pid) ?? throw RpcException.NotFound(Loc.S("İşlem artık çalışmıyor.", "The process is no longer running."));
        if (p.Protected || pid <= 4 || pid == Environment.ProcessId)
            throw RpcException.Denied(Loc.S("Bu işlem Windows'un çalışması için gerekli; OptiCore ona dokunmaz.", "Windows needs this process to run; OptiCore will not touch it."));
        return p;
    }

    private object Kill(int pid, bool tree)
    {
        var target = Target(pid);
        var snapshot = _sampler.Snapshot();
        var victims = new List<ProcInfo> { target };
        if (tree)
        {
            var queue = new Queue<ProcInfo>([target]);
            while (queue.Count > 0)
            {
                var parent = queue.Dequeue();
                foreach (var child in snapshot.Where(c => c.ParentPid == parent.Pid && c.Started >= parent.Started && !c.Protected && !victims.Contains(c)))
                {
                    victims.Add(child);
                    queue.Enqueue(child);
                }
            }
        }
        int killed = 0;
        using var scope = _journal.Begin("processes", Loc.T("İşlem sonlandırıldı: {0}", "Process ended: {0}").F(target.Name));
        foreach (var v in Enumerable.Reverse(victims))
        {
            scope.Note("process.kill", $"{v.Name} (PID {v.Pid})", new() { ["path"] = v.Path });
            if (scope.DryRun) continue;
            try
            {
                WithHandle(v.Pid, Win32.PROCESS_TERMINATE, h => Win32.TerminateProcess(h, 1) ? true : throw new Win32Exception(Marshal.GetLastWin32Error()));
                killed++;
            }
            catch (RpcException) { /* already gone */ }
        }
        return new { killed, requested = victims.Count, simulated = scope.DryRun };
    }

    private object Details(int pid)
    {
        var snapshot = _sampler.Snapshot();
        var p = snapshot.FirstOrDefault(x => x.Pid == pid) ?? throw RpcException.NotFound(Loc.S("İşlem artık çalışmıyor.", "The process is no longer running."));
        string? commandLine = null;
        foreach (var row in Wmi.Query($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}")) commandLine = row.S("CommandLine");

        string? priority = null; bool? eco = null; long? affinity = null;
        IntPtr h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                priority = PriorityName(Win32.GetPriorityClass(h));
                var state = new Win32.PROCESS_POWER_THROTTLING_STATE { Version = 1 };
                if (Win32.GetProcessInformation(h, Win32.ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<Win32.PROCESS_POWER_THROTTLING_STATE>()))
                    eco = (state.StateMask & Win32.PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0;
                if (Win32.GetProcessAffinityMask(h, out var mask, out _)) affinity = (long)mask;
            }
            finally { Win32.CloseHandle(h); }
        }

        var sig = Signature.Check(p.Path);
        var chain = new List<object>();
        var cursor = p;
        for (int i = 0; i < 12; i++)
        {
            var parent = snapshot.FirstOrDefault(x => x.Pid == cursor.ParentPid && x.Started <= cursor.Started);
            if (parent is null || parent.Pid == cursor.Pid) break;
            chain.Add(new { parent.Pid, parent.Name });
            cursor = parent;
        }
        string? version = null; long? fileSize = null; DateTime? fileDate = null;
        if (p.Path is not null && File.Exists(p.Path))
        {
            try { version = FileVersionInfo.GetVersionInfo(p.Path).FileVersion; var fi = new FileInfo(p.Path); fileSize = fi.Length; fileDate = fi.LastWriteTimeUtc; } catch { /* unreadable */ }
        }
        return new
        {
            process = p, commandLine, priority, eco, affinity, version, fileSize, fileDate,
            signature = new { sig.Signed, sig.Trusted, sig.Signer },
            parents = chain,
            children = snapshot.Where(c => c.ParentPid == pid && c.Started >= p.Started).Select(c => new { c.Pid, c.Name, c.Cpu, c.Memory }),
            connections = ConnectionTable.Snapshot().Where(c => c.Pid == pid).Take(200),
            icon = Icons.DataUrl(p.Path),
        };
    }

    /// <summary>Restart Manager: which processes keep these files open.</summary>
    public static List<object> WhoLocks(string[] paths)
    {
        var result = new List<object>();
        var key = new StringBuilder(64);
        if (Win32.RmStartSession(out uint session, 0, key) != 0) throw RpcException.Failed("Restart Manager session could not be started");
        try
        {
            if (Win32.RmRegisterResources(session, (uint)paths.Length, paths, 0, IntPtr.Zero, 0, null) != 0)
                throw RpcException.Failed(Loc.S("Dosya incelenemedi.", "The file could not be inspected."));
            uint count = 0;
            int rc = Win32.RmGetList(session, out uint needed, ref count, null, out _);
            if (rc == 0 || needed == 0) return result;
            if (rc != 234) throw RpcException.Failed($"RmGetList failed ({rc})"); // ERROR_MORE_DATA
            var infos = new Win32.RM_PROCESS_INFO[needed];
            count = needed;
            if (Win32.RmGetList(session, out needed, ref count, infos, out _) != 0) return result;
            for (int i = 0; i < count; i++)
            {
                int pid = infos[i].Process.dwProcessId;
                string? path = ProcessSampler.QueryPath(pid);
                result.Add(new { pid, name = infos[i].strAppName, service = infos[i].strServiceShortName, path, type = infos[i].ApplicationType, icon = Icons.DataUrl(path) });
            }
        }
        finally { Win32.RmEndSession(session); }
        return result;
    }

    // ------------------------------------------------------------------ rpc
    public void Register(RpcRouter rpc)
    {
        rpc.On("processes.list", _ =>
        {
            var items = _sampler.Snapshot();
            return new { items, cores = Environment.ProcessorCount, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        });
        rpc.On("processes.top", c =>
        {
            int n = c.Int("n", 6);
            var groups = _sampler.Snapshot(2500).Where(p => p.Pid > 4).GroupBy(GroupOf).Select(g => new
            {
                name = g.Key, count = g.Count(), cpu = g.Sum(p => p.Cpu), memory = g.Sum(p => p.Memory), gpu = g.Max(p => p.Gpu),
                path = g.OrderByDescending(p => p.Memory).First().Path, pid = g.OrderByDescending(p => p.Cpu).First().Pid,
            }).ToList();
            return new
            {
                cpu = groups.OrderByDescending(g => g.cpu).Take(n),
                memory = groups.OrderByDescending(g => g.memory).Take(n),
            };
        });
        rpc.On("processes.details", c => Details(c.Int("pid")));
        rpc.On("processes.kill", c => Kill(c.Int("pid"), c.Bool("tree")));
        rpc.Do("processes.priority", c =>
        {
            var p = Target(c.Int("pid"));
            string level = c.Str("level");
            using var scope = _journal.Begin("processes", Loc.T("{0} önceliği: {1}", "{0} priority: {1}").F(p.Name, level));
            scope.Note("process.priority", $"{p.Name} (PID {p.Pid})", new() { ["level"] = level });
            if (!scope.DryRun) SetPriority(p.Pid, level);
        });
        rpc.Do("processes.eco", c =>
        {
            var p = Target(c.Int("pid"));
            bool on = c.Bool("on", true);
            using var scope = _journal.Begin("processes", Loc.T("{0}: verimlilik modu {1}", "{0}: efficiency mode {1}").F(p.Name, on ? "on" : "off"));
            scope.Note("process.eco", $"{p.Name} (PID {p.Pid})", new() { ["on"] = on ? "1" : "0" });
            if (!scope.DryRun) SetEco(p.Pid, on);
        });
        rpc.Do("processes.suspend", c =>
        {
            var p = Target(c.Int("pid"));
            bool resume = c.Bool("resume");
            if (_journal.DryRun) return;
            WithHandle(p.Pid, Win32.PROCESS_SUSPEND_RESUME, h => resume ? Win32.NtResumeProcess(h) : Win32.NtSuspendProcess(h));
        });
        rpc.On("processes.history", c =>
        {
            string name = c.Str("name");
            lock (_gate)
                return _tracks.TryGetValue(name, out var t)
                    ? t.Points.Select(p => new { t = p.T, cpu = p.Cpu, mem = p.Mem }).ToList()
                    : [];
        });
        rpc.On("processes.usage", c =>
        {
            // Per-application CPU time and average memory for the last N days.
            int days = Math.Clamp(c.Int("days", 1), 1, 30);
            var total = new Dictionary<string, (double Cpu, long Mem, int Samples)>(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                foreach (var (name, v) in _daily) total[name] = v;
                for (int d = 1; d < days; d++)
                    foreach (var (name, v) in _store.Load(DailyFile(DateTime.Now.AddDays(-d).ToString("yyyyMMdd")), () => new Dictionary<string, double[]>()))
                    {
                        if (v.Length != 3) continue;
                        var cur = total.GetValueOrDefault(name);
                        total[name] = (cur.Cpu + v[0], cur.Mem + (long)v[1], cur.Samples + (int)v[2]);
                    }
            }
            return total.Select(t => new { name = t.Key, cpuSeconds = t.Value.Cpu, avgMemoryMb = t.Value.Mem / Math.Max(1, t.Value.Samples), samples = t.Value.Samples })
                .OrderByDescending(x => x.cpuSeconds).Take(40).ToList();
        });
        rpc.On("processes.rules.list", _ => { lock (_gate) return _rules.ToList(); });
        rpc.On("processes.rules.save", c =>
        {
            var rule = c.Get<ProcessRule>("rule");
            if (rule.Priority.Length > 0) PriorityClass(rule.Priority);
            if (string.IsNullOrWhiteSpace(rule.Match)) throw RpcException.Invalid("Rule needs an executable name");
            if (ProcessSampler.IsCritical(rule.Match)) throw RpcException.Denied(Loc.S("Sistem işlemleri için kural tanımlanamaz.", "Rules cannot target system processes."));
            lock (_gate)
            {
                _rules.RemoveAll(r => r.Id == rule.Id);
                _rules.Add(rule);
                _store.Save(Path.Combine("processes", "rules.json"), _rules);
                return _rules.ToList();
            }
        });
        rpc.On("processes.rules.delete", c =>
        {
            string id = c.Str("id");
            lock (_gate)
            {
                _rules.RemoveAll(r => r.Id == id);
                _store.Save(Path.Combine("processes", "rules.json"), _rules);
                return _rules.ToList();
            }
        });
        rpc.On("processes.connections", _ =>
        {
            var names = _sampler.Snapshot(5000).ToDictionary(p => p.Pid, p => p);
            return ConnectionTable.Snapshot().Select(c => new
            {
                c.Pid, c.Protocol, c.LocalAddress, c.LocalPort, c.RemoteAddress, c.RemotePort, c.State,
                process = names.TryGetValue(c.Pid, out var p) ? p.Name : "?", path = names.TryGetValue(c.Pid, out var q) ? q.Path : null,
            }).ToList();
        });
        rpc.On("processes.whoLocks", c => WhoLocks(c.StrArr("paths")));
        rpc.On("icons.get", c => c.StrArr("paths").Distinct().Take(400).ToDictionary(p => p, Icons.DataUrl));
    }

    public void Flush() { lock (_gate) FlushDaily(); }
}
