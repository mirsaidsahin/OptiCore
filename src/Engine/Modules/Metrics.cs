using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using OptiCore.Engine.Core;
using OptiCore.Engine.Native;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public sealed class GpuAdapter
{
    /// <summary>"0xHHHHHHHH_0xLLLLLLLL": the same form Windows uses in GPU performance counter instance names.</summary>
    public string Luid { get; set; } = "";
    public string Name { get; set; } = "";
    public long DedicatedBytes { get; set; }
    public long SharedBytes { get; set; }
}

/// <summary>GPU inventory and thermals through D3DKMT, the same kernel interface Task Manager uses. No vendor SDK, no driver.</summary>
public static unsafe class GpuProbe
{
    private static List<GpuAdapter>? _adapters;
    public static List<GpuAdapter> Adapters() => _adapters ??= Walk(null);

    public sealed record Perf(double? TempC, int? FanRpm, double? PowerPercent);

    public static Dictionary<string, Perf> ReadPerf()
    {
        var result = new Dictionary<string, Perf>();
        Walk((luid, handle) =>
        {
            var data = new Win32.D3DKMT_ADAPTER_PERFDATA();
            var q = new Win32.D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = handle, Type = Win32.KMTQAITYPE_ADAPTERPERFDATA,
                pPrivateDriverData = (IntPtr)(&data), PrivateDriverDataSize = (uint)sizeof(Win32.D3DKMT_ADAPTER_PERFDATA),
            };
            if (Win32.D3DKMTQueryAdapterInfo(ref q) != 0) return;
            // Temperature is reported in tenths of a degree Celsius; 0 means the driver does not expose it.
            double? temp = data.Temperature is > 0 and < 2000 ? data.Temperature / 10.0 : null;
            result[luid] = new Perf(temp, data.FanRPM is > 0 and < 20000 ? (int)data.FanRPM : null, data.Power is > 0 and <= 2000 ? data.Power / 10.0 : null);
        });
        return result;
    }

    private static List<GpuAdapter> Walk(Action<string, uint>? visit)
    {
        var list = new List<GpuAdapter>();
        try
        {
            var e = new Win32.D3DKMT_ENUMADAPTERS2();
            if (Win32.D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return list;
            int size = sizeof(Win32.D3DKMT_ADAPTERINFO);
            IntPtr buffer = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
            try
            {
                e.pAdapters = buffer;
                if (Win32.D3DKMTEnumAdapters2(ref e) != 0) return list;
                for (int i = 0; i < e.NumAdapters; i++)
                {
                    var info = *(Win32.D3DKMT_ADAPTERINFO*)(buffer + i * size);
                    try
                    {
                        string name = QueryName(info.hAdapter);
                        if (name.Length == 0 || name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) continue;
                        string luid = $"0x{info.AdapterLuid.HighPart:X8}_0x{info.AdapterLuid.LowPart:X8}";
                        if (visit is not null) { visit(luid, info.hAdapter); continue; }
                        var (dedicated, shared) = QuerySegments(info.hAdapter);
                        if (list.Any(a => a.Name == name && a.DedicatedBytes == dedicated)) continue;
                        list.Add(new GpuAdapter { Luid = luid, Name = name, DedicatedBytes = dedicated, SharedBytes = shared });
                    }
                    finally
                    {
                        var close = new Win32.D3DKMT_CLOSEADAPTER { hAdapter = info.hAdapter };
                        Win32.D3DKMTCloseAdapter(ref close);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex) { Log.Warn("gpu: enumeration failed", ex); }
        return list;
    }

    private static string QueryName(uint adapter)
    {
        const int size = 260 * 2 * 4; // D3DKMT_ADAPTERREGISTRYINFO: four WCHAR[MAX_PATH] strings
        byte* p = (byte*)NativeMemory.AllocZeroed(size);
        try
        {
            var q = new Win32.D3DKMT_QUERYADAPTERINFO { hAdapter = adapter, Type = 8, pPrivateDriverData = (IntPtr)p, PrivateDriverDataSize = size };
            return Win32.D3DKMTQueryAdapterInfo(ref q) == 0 ? (Marshal.PtrToStringUni((IntPtr)p) ?? "").Trim() : "";
        }
        finally { NativeMemory.Free(p); }
    }

    private static (long Dedicated, long Shared) QuerySegments(uint adapter)
    {
        long* p = stackalloc long[3]; // D3DKMT_SEGMENTSIZEINFO: dedicated video, dedicated system, shared system
        p[0] = p[1] = p[2] = 0;
        var q = new Win32.D3DKMT_QUERYADAPTERINFO { hAdapter = adapter, Type = 3, pPrivateDriverData = (IntPtr)p, PrivateDriverDataSize = 24 };
        return Win32.D3DKMTQueryAdapterInfo(ref q) == 0 ? (p[0], p[2]) : (0, 0);
    }
}

/// <summary>Separates real network adapters from NDIS filter layers and virtual interfaces.</summary>
public static class NetAdapters
{
    private static readonly string[] VirtualHints = ["virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "loopback", "tap-", "wintun", "wireguard", "tunnel", "bluetooth", "npcap", "pseudo", "wi-fi direct"];
    private static readonly string[] Noise = ["wan miniport", "teredo", "isatap", "6to4", "kernel debug", "-qos packet scheduler", "lightweight filter", "filter driver"];

    /// <summary>
    /// Windows also lists every filter layer bound to an adapter ("Wi-Fi-QoS Packet Scheduler-0000").
    /// They mirror the adapter's counters, so counting them would multiply the traffic.
    /// </summary>
    public static bool IsReal(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;
        string text = (nic.Description + " " + nic.Name).ToLowerInvariant();
        if (Noise.Any(text.Contains)) return false;
        string d = nic.Description ?? "";
        return !(d.Length > 5 && d[^5] == '-' && d[^4..].All(char.IsDigit));
    }

    public static bool IsPhysical(NetworkInterface nic)
    {
        if (!IsReal(nic)) return false;
        if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2)) return false;
        string text = (nic.Description + " " + nic.Name).ToLowerInvariant();
        return !VirtualHints.Any(text.Contains);
    }
}

public sealed class DiskIo { public string Name { get; set; } = ""; public double Read { get; set; } public double Write { get; set; } public double Active { get; set; } public double LatencyMs { get; set; } public double Queue { get; set; } }
public sealed class NetIo { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Description { get; set; } = ""; public string Type { get; set; } = ""; public double Down { get; set; } public double Up { get; set; } public long SpeedBps { get; set; } public bool Physical { get; set; } }
public sealed class GpuLive { public string Name { get; set; } = ""; public double Usage { get; set; } public long MemUsed { get; set; } public long MemTotal { get; set; } public long SharedUsed { get; set; } public long SharedTotal { get; set; } public double? Temp { get; set; } public int? FanRpm { get; set; } public Dictionary<string, double> Engines { get; set; } = []; }
public sealed class ZoneTemp
{
    public string Name { get; set; } = "";
    public double TempC { get; set; }
    public double PassiveLimit { get; set; } = 100;
    /// <summary>The zone always reports the same value: a firmware placeholder, not a sensor. Ignored for the headline temperature.</summary>
    public bool Static { get; set; }
}

/// <summary>
/// Finds ACPI thermal zones that never move. Many laptops expose one zone that is a constant (for example 27.85 °C)
/// because the real sensor sits behind the embedded controller. A zone is marked static after 60 samples spanning
/// less than 0.2 °C; the marks are remembered so the next start excludes them at once, and a mark is dropped when
/// the zone later reports a value at least 1 °C away from the remembered one.
/// </summary>
internal sealed class StaticZoneTracker
{
    public const int MinSamples = 60;
    public const double MaxSpan = 0.2;
    public const double ReleaseDelta = 1.0;

    private sealed class Stat { public double Min = double.MaxValue, Max = double.MinValue; public int Count; }

    private readonly Dictionary<string, Stat> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _known;

    public StaticZoneTracker(IDictionary<string, double>? known = null) =>
        _known = new Dictionary<string, double>(known ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the remembered set changed and should be saved.</summary>
    public bool Dirty { get; private set; }
    public IReadOnlyDictionary<string, double> Known => _known;
    public void MarkSaved() => Dirty = false;

    /// <summary>Records one reading and tells whether the zone counts as static.</summary>
    public bool Observe(string zone, double celsius)
    {
        if (_known.TryGetValue(zone, out var value))
        {
            if (Math.Abs(celsius - value) < ReleaseDelta) return true;
            _known.Remove(zone);
            _stats.Remove(zone);
            Dirty = true;
        }
        if (!_stats.TryGetValue(zone, out var st)) _stats[zone] = st = new Stat();
        st.Count++;
        st.Min = Math.Min(st.Min, celsius);
        st.Max = Math.Max(st.Max, celsius);
        if (st.Count >= MinSamples && st.Max - st.Min < MaxSpan)
        {
            _known[zone] = Math.Round(celsius, 2);
            Dirty = true;
            return true;
        }
        return false;
    }
}

/// <summary>Reads the CPU temperature from LibreHardwareMonitor / OpenHardwareMonitor when one of them is running (they publish sensors through WMI).</summary>
internal sealed class HardwareMonitorTemp
{
    private static readonly (string Scope, string Source)[] Namespaces = [(@"root\LibreHardwareMonitor", "lhm"), (@"root\OpenHardwareMonitor", "ohm")];
    private const long QueryEveryMs = 5000, AbsentForMs = 60000, StaleAfterMs = 15000;

    private readonly long[] _skipUntil = new long[Namespaces.Length];
    private long _lastQuery = long.MinValue / 2;
    private int _busy;
    private (double Temp, string Source, long At)? _value;

    /// <summary>Latest cached reading; starts a refresh in the background when due. Never blocks the caller.</summary>
    public (double Temp, string Source)? Current()
    {
        long now = Environment.TickCount64;
        if (now - _lastQuery >= QueryEveryMs && Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
        {
            _lastQuery = now;
            Task.Run(() => { try { Refresh(); } catch { /* optional data */ } finally { Volatile.Write(ref _busy, 0); } });
        }
        var v = _value;
        return v is { } x && now - x.At < StaleAfterMs ? (x.Temp, x.Source) : null;
    }

    private void Refresh()
    {
        for (int i = 0; i < Namespaces.Length; i++)
        {
            long now = Environment.TickCount64;
            if (now < _skipUntil[i]) continue;
            var rows = Wmi.Query("SELECT Name, Value, Identifier FROM Sensor WHERE SensorType='Temperature'", Namespaces[i].Scope, out var error, 5, quiet: true);
            if (error is not null) { _skipUntil[i] = now + AbsentForMs; continue; }
            var picked = Pick(rows.Select(r => (r.S("Name"), r.D("Value"), r.S("Identifier"))));
            if (picked is double t) { _value = (t, Namespaces[i].Source, Environment.TickCount64); return; }
            _skipUntil[i] = now + QueryEveryMs;
        }
        _value = null;
    }

    /// <summary>Chooses the CPU temperature: CPU Package, then Core (Tctl/Tdie), Core Average, Core Max, then the hottest "CPU Core #n".</summary>
    public static double? Pick(IEnumerable<(string Name, double Value, string Id)> sensors)
    {
        var list = sensors.Where(s => s.Value is > 1 and < 150 && (s.Id.Length == 0 || s.Id.Contains("cpu", StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var name in new[] { "CPU Package", "Core (Tctl/Tdie)", "Core Average", "Core Max" })
        {
            var hit = list.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit.Name is not null) return hit.Value;
        }
        var cores = list.Where(s => s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase)).ToList();
        return cores.Count > 0 ? cores.Max(s => s.Value) : null;
    }
}

/// <summary>One second of system activity.</summary>
public sealed class LiveSample
{
    public long T { get; set; }
    public double Cpu { get; set; }
    public double[] Cores { get; set; } = [];
    public double CpuMhz { get; set; }
    /// <summary>Current speed relative to the nominal frequency (above 100 = turbo, below 100 under load = throttled).</summary>
    public double CpuPerf { get; set; }
    public double CpuQueue { get; set; }
    public long MemTotal { get; set; }
    public long MemUsed { get; set; }
    public long MemAvailable { get; set; }
    public long MemCached { get; set; }
    public long Commit { get; set; }
    public long CommitLimit { get; set; }
    public double MemPercent { get; set; }
    public double HardFaults { get; set; }
    public double PageFilePercent { get; set; }
    public double Gpu { get; set; }
    public List<GpuLive> Gpus { get; set; } = [];
    public double DiskActive { get; set; }
    public double DiskRead { get; set; }
    public double DiskWrite { get; set; }
    public List<DiskIo> Disks { get; set; } = [];
    public double NetDown { get; set; }
    public double NetUp { get; set; }
    public List<NetIo> Nets { get; set; } = [];
    /// <summary>Hottest temperature Windows exposes without a kernel driver (ACPI thermal zones, GPU).</summary>
    public double? Temp { get; set; }
    /// <summary>Where <see cref="Temp"/> came from: "lhm" | "ohm" (LibreHardwareMonitor / OpenHardwareMonitor), "acpi", "gpu", or null when nothing reports a temperature.</summary>
    public string? TempSource { get; set; }
    public List<ZoneTemp> Zones { get; set; } = [];
    public bool Throttled { get; set; }
    public bool OnBattery { get; set; }
    public int BatteryPercent { get; set; } = -1;
    public int Processes { get; set; }
    public int Threads { get; set; }
    public int Handles { get; set; }
}

public sealed class MinuteStat
{
    public long T { get; set; }
    public double Cpu { get; set; }
    public double CpuMax { get; set; }
    public double Mem { get; set; }
    public double Gpu { get; set; }
    public double Disk { get; set; }
    public double Down { get; set; }
    public double Up { get; set; }
    public double? Temp { get; set; }
}

/// <summary>
/// Collects live metrics on a dedicated low-priority thread. Samples every second while the UI is
/// watching and every five seconds otherwise, so OptiCore itself stays out of the way.
/// </summary>
public sealed class MetricsSampler : IDisposable
{
    private const int RingSize = 300;
    private readonly PdhQuery _pdh = new();
    private readonly DataStore _store;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly LiveSample?[] _ring = new LiveSample?[RingSize];
    private readonly object _gate = new();
    private int _head;
    private volatile bool _stop;
    private long _lastDemand;
    private double _nominalMhz;
    private volatile Dictionary<int, double> _gpuByPid = [];

    private NetworkInterface[] _nics = [];
    private long _nicsAt;
    private readonly Dictionary<string, (long Rx, long Tx)> _nicPrev = [];
    private long _prevTicks;
    private Dictionary<string, GpuProbe.Perf> _gpuPerf = [];
    private int _tick;
    private readonly StaticZoneTracker _zoneTracker;
    private readonly HardwareMonitorTemp _hwTemp = new();
    private const string StaticZonesFile = "metrics/static-zones.json";

    private readonly List<LiveSample> _minute = [];
    private int _minuteOf = -1;

    public LiveSample? Latest { get; private set; }
    public IReadOnlyDictionary<int, double> GpuByPid => _gpuByPid;
    public event Action<LiveSample>? Sampled;

    public MetricsSampler(DataStore store)
    {
        _store = store;
        _zoneTracker = new StaticZoneTracker(store.Load(StaticZonesFile, () => new Dictionary<string, double>()));
        _nominalMhz = Reg.GetInt(@"HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz") ?? 0;
        // "% Processor Performance" is relative to Windows' base speed, which WMI knows (slow query, so off-thread).
        Task.Run(() =>
        {
            foreach (var row in Wmi.Query("SELECT MaxClockSpeed FROM Win32_Processor"))
                if (row.I("MaxClockSpeed") > 0) Volatile.Write(ref _nominalMhz, row.I("MaxClockSpeed"));
        });

        if (!_pdh.Add("cpu", @"\Processor Information(*)\% Processor Utility")) _pdh.Add("cpu", @"\Processor(*)\% Processor Time");
        _pdh.Add("perf", @"\Processor Information(_Total)\% Processor Performance");
        _pdh.Add("queue", @"\System\Processor Queue Length");
        _pdh.Add("pagesIn", @"\Memory\Pages Input/sec");
        _pdh.Add("pageFile", @"\Paging File(_Total)\% Usage");
        _pdh.Add("gpuEngine", @"\GPU Engine(*)\Utilization Percentage");
        _pdh.Add("gpuDed", @"\GPU Adapter Memory(*)\Dedicated Usage");
        _pdh.Add("gpuShared", @"\GPU Adapter Memory(*)\Shared Usage");
        _pdh.Add("dRead", @"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _pdh.Add("dWrite", @"\PhysicalDisk(*)\Disk Write Bytes/sec");
        _pdh.Add("dIdle", @"\PhysicalDisk(*)\% Idle Time");
        _pdh.Add("dLat", @"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
        _pdh.Add("dQueue", @"\PhysicalDisk(*)\Current Disk Queue Length");
        _pdh.Add("tzTemp", @"\Thermal Zone Information(*)\High Precision Temperature");
        _pdh.Add("tzPassive", @"\Thermal Zone Information(*)\% Passive Limit");
        _pdh.Collect();

        _prevTicks = Stopwatch.GetTimestamp();
        _thread = new Thread(Loop) { IsBackground = true, Name = "OptiCore.Metrics", Priority = ThreadPriority.BelowNormal };
    }

    public void Start() { if (!_thread.IsAlive) _thread.Start(); }

    /// <summary>Tells the sampler somebody is watching, switching it to one-second resolution.</summary>
    public void Touch()
    {
        long now = Environment.TickCount64;
        bool wasIdle = now - Interlocked.Exchange(ref _lastDemand, now) > 6000;
        if (wasIdle) _wake.Set();
    }

    private void Loop()
    {
        while (!_stop)
        {
            bool watched = Environment.TickCount64 - Interlocked.Read(ref _lastDemand) < 15000;
            _wake.Wait(watched ? 1000 : 5000);
            _wake.Reset();
            if (_stop) break;
            try { Tick(); }
            catch (Exception e) { Log.Warn("metrics: tick failed", e); }
        }
    }

    private void Tick()
    {
        long ticks = Stopwatch.GetTimestamp();
        double dt = Math.Max(0.2, (ticks - _prevTicks) / (double)Stopwatch.Frequency);
        _prevTicks = ticks;
        _tick++;
        _pdh.Collect();

        var s = new LiveSample { T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        SampleCpu(s);
        SampleMemory(s);
        SampleGpu(s);
        SampleDisks(s);
        SampleNetwork(s, dt);
        SampleThermal(s);

        if (Win32.GetSystemPowerStatus(out var power))
        {
            s.OnBattery = power.ACLineStatus == 0;
            s.BatteryPercent = power.BatteryLifePercent <= 100 && (power.BatteryFlag & 128) == 0 ? power.BatteryLifePercent : -1;
        }

        lock (_gate)
        {
            _ring[_head] = s;
            _head = (_head + 1) % RingSize;
            Latest = s;
        }
        Aggregate(s);
        try { Sampled?.Invoke(s); } catch (Exception e) { Log.Warn("metrics: listener failed", e); }
    }

    private void SampleCpu(LiveSample s)
    {
        var cpu = _pdh.Array("cpu");
        var cores = new List<(int Group, int Index, double Value)>();
        foreach (var (name, value) in cpu)
        {
            if (name == "_Total") { s.Cpu = Math.Clamp(value, 0, 100); continue; }
            if (name.Contains("_Total", StringComparison.Ordinal)) continue;
            var parts = name.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out int g) && int.TryParse(parts[1], out int i)) cores.Add((g, i, Math.Clamp(value, 0, 100)));
            else if (int.TryParse(name, out int single)) cores.Add((0, single, Math.Clamp(value, 0, 100)));
        }
        s.Cores = [.. cores.OrderBy(c => c.Group).ThenBy(c => c.Index).Select(c => c.Value)];
        if (cpu.Count > 0 && !cpu.ContainsKey("_Total") && s.Cores.Length > 0) s.Cpu = s.Cores.Average();
        s.CpuPerf = _pdh.Value("perf") ?? 0;
        double nominal = Volatile.Read(ref _nominalMhz);
        s.CpuMhz = nominal > 0 && s.CpuPerf > 0 ? nominal * s.CpuPerf / 100.0 : nominal;
        s.CpuQueue = _pdh.Value("queue") ?? 0;
    }

    private void SampleMemory(LiveSample s)
    {
        if (Win32.GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<Win32.PERFORMANCE_INFORMATION>()))
        {
            long page = (long)pi.PageSize;
            s.MemTotal = (long)pi.PhysicalTotal * page;
            s.MemAvailable = (long)pi.PhysicalAvailable * page;
            s.MemUsed = s.MemTotal - s.MemAvailable;
            s.MemCached = (long)pi.SystemCache * page;
            s.Commit = (long)pi.CommitTotal * page;
            s.CommitLimit = (long)pi.CommitLimit * page;
            s.MemPercent = s.MemTotal > 0 ? 100.0 * s.MemUsed / s.MemTotal : 0;
            s.Processes = (int)pi.ProcessCount;
            s.Threads = (int)pi.ThreadCount;
            s.Handles = (int)pi.HandleCount;
        }
        s.HardFaults = _pdh.Value("pagesIn") ?? 0;
        s.PageFilePercent = _pdh.Value("pageFile") ?? 0;
    }

    private void SampleGpu(LiveSample s)
    {
        var adapters = GpuProbe.Adapters();
        if (_tick % 3 == 1) { try { _gpuPerf = GpuProbe.ReadPerf(); } catch { /* optional data */ } }

        // Instance: pid_1234_luid_0x00000000_0x0000C7B5_phys_0_eng_0_engtype_3D
        var perAdapter = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        var perPid = new Dictionary<int, Dictionary<string, double>>();
        foreach (var (name, value) in _pdh.Array("gpuEngine"))
        {
            if (value <= 0) continue;
            int luidAt = name.IndexOf("luid_", StringComparison.Ordinal);
            int physAt = name.IndexOf("_phys_", StringComparison.Ordinal);
            int typeAt = name.IndexOf("engtype_", StringComparison.Ordinal);
            if (luidAt < 0 || physAt < luidAt || typeAt < 0) continue;
            string luid = name[(luidAt + 5)..physAt];
            string type = name[(typeAt + 8)..];
            if (!perAdapter.TryGetValue(luid, out var engines)) perAdapter[luid] = engines = [];
            engines[type] = engines.GetValueOrDefault(type) + value;
            if (name.StartsWith("pid_", StringComparison.Ordinal))
            {
                int end = name.IndexOf('_', 4);
                if (end > 4 && int.TryParse(name.AsSpan(4, end - 4), out int pid))
                {
                    if (!perPid.TryGetValue(pid, out var pe)) perPid[pid] = pe = [];
                    pe[type] = pe.GetValueOrDefault(type) + value;
                }
            }
        }
        _gpuByPid = perPid.ToDictionary(p => p.Key, p => Math.Min(100, p.Value.Values.Max()));

        var dedicated = _pdh.Array("gpuDed");
        var shared = _pdh.Array("gpuShared");
        foreach (var a in adapters)
        {
            var live = new GpuLive { Name = a.Name, MemTotal = a.DedicatedBytes, SharedTotal = a.SharedBytes };
            if (perAdapter.TryGetValue(a.Luid, out var engines))
            {
                live.Engines = engines.Where(e => e.Value >= 0.5).ToDictionary(e => e.Key, e => Math.Min(100, e.Value));
                live.Usage = Math.Min(100, engines.Values.Max());
            }
            live.MemUsed = (long)dedicated.Where(d => d.Key.Contains(a.Luid, StringComparison.OrdinalIgnoreCase)).Sum(d => d.Value);
            live.SharedUsed = (long)shared.Where(d => d.Key.Contains(a.Luid, StringComparison.OrdinalIgnoreCase)).Sum(d => d.Value);
            if (_gpuPerf.TryGetValue(a.Luid, out var perf)) { live.Temp = perf.TempC; live.FanRpm = perf.FanRpm; }
            s.Gpus.Add(live);
        }
        s.Gpu = s.Gpus.Count > 0 ? s.Gpus.Max(g => g.Usage) : perAdapter.Count > 0 ? Math.Min(100, perAdapter.Values.Max(e => e.Values.Max())) : 0;
    }

    private void SampleDisks(LiveSample s)
    {
        var read = _pdh.Array("dRead");
        var write = _pdh.Array("dWrite");
        var idle = _pdh.Array("dIdle");
        var latency = _pdh.Array("dLat");
        var queue = _pdh.Array("dQueue");
        foreach (var name in read.Keys.Where(k => k != "_Total").OrderBy(k => k))
        {
            var d = new DiskIo
            {
                Name = name,
                Read = Math.Max(0, read.GetValueOrDefault(name)),
                Write = Math.Max(0, write.GetValueOrDefault(name)),
                Active = idle.TryGetValue(name, out var i) ? Math.Clamp(100 - i, 0, 100) : 0,
                LatencyMs = Math.Max(0, latency.GetValueOrDefault(name)) * 1000,
                Queue = Math.Max(0, queue.GetValueOrDefault(name)),
            };
            s.Disks.Add(d);
        }
        s.DiskRead = s.Disks.Sum(d => d.Read);
        s.DiskWrite = s.Disks.Sum(d => d.Write);
        s.DiskActive = s.Disks.Count > 0 ? s.Disks.Max(d => d.Active) : 0;
    }

    private void SampleNetwork(LiveSample s, double dt)
    {
        long now = Environment.TickCount64;
        if (_nics.Length == 0 || now - _nicsAt > 15000)
        {
            try { _nics = [.. NetworkInterface.GetAllNetworkInterfaces().Where(n => NetAdapters.IsReal(n) && n.OperationalStatus == OperationalStatus.Up)]; }
            catch { _nics = []; }
            _nicsAt = now;
        }
        foreach (var nic in _nics)
        {
            try
            {
                var st = nic.GetIPStatistics();
                long rx = st.BytesReceived, tx = st.BytesSent;
                double down = 0, up = 0;
                if (_nicPrev.TryGetValue(nic.Id, out var prev) && rx >= prev.Rx && tx >= prev.Tx)
                {
                    down = (rx - prev.Rx) / dt;
                    up = (tx - prev.Tx) / dt;
                }
                _nicPrev[nic.Id] = (rx, tx);
                bool physical = NetAdapters.IsPhysical(nic);
                s.Nets.Add(new NetIo
                {
                    Id = nic.Id, Name = nic.Name, Description = nic.Description ?? "", Down = down, Up = up, Physical = physical,
                    Type = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi" : physical ? "ethernet" : "virtual",
                    SpeedBps = nic.Speed > 0 ? nic.Speed : 0,
                });
            }
            catch { /* adapter vanished between enumeration and query */ }
        }
        var counted = s.Nets.Any(n => n.Physical) ? s.Nets.Where(n => n.Physical) : s.Nets;
        s.NetDown = counted.Sum(n => n.Down);
        s.NetUp = counted.Sum(n => n.Up);
    }

    private void SampleThermal(LiveSample s)
    {
        var passive = _pdh.Array("tzPassive");
        foreach (var (name, value) in _pdh.Array("tzTemp"))
        {
            double c = value / 10.0 - 273.15;
            if (c is < 1 or > 150) continue;
            s.Zones.Add(new ZoneTemp { Name = name, TempC = c, PassiveLimit = passive.TryGetValue(name, out var p) ? p : 100 });
        }
        foreach (var z in s.Zones) z.Static = _zoneTracker.Observe(z.Name, z.TempC);
        if (_zoneTracker.Dirty) { _store.Save(StaticZonesFile, _zoneTracker.Known); _zoneTracker.MarkSaved(); }

        // A running LibreHardwareMonitor/OpenHardwareMonitor gives the real CPU temperature; otherwise the hottest zone
        // that is an actual sensor. The graphics card still counts when it reports one.
        double? temp = null;
        string? source = null;
        if (_hwTemp.Current() is { } hw) { temp = hw.Temp; source = hw.Source; }
        else if (s.Zones.Where(z => !z.Static).Select(z => (double?)z.TempC).Max() is double acpi) { temp = acpi; source = "acpi"; }
        foreach (var g in s.Gpus) if (g.Temp is double t && (temp is null || t > temp)) { temp = t; source = "gpu"; }
        s.Temp = temp;
        s.TempSource = source;
        // Firmware lowers "% Passive Limit" below 100 when it is actively capping the CPU to shed heat.
        s.Throttled = s.Zones.Any(z => z.PassiveLimit < 99.5) || (s.Cpu > 85 && s.CpuPerf is > 0 and < 95);
    }

    private void Aggregate(LiveSample s)
    {
        int minute = DateTime.UtcNow.Minute;
        if (_minuteOf == -1) _minuteOf = minute;
        if (minute != _minuteOf && _minute.Count > 0)
        {
            var temps = _minute.Where(x => x.Temp is not null).Select(x => x.Temp!.Value).ToList();
            var stat = new MinuteStat
            {
                T = _minute[0].T / 60000 * 60000,
                Cpu = _minute.Average(x => x.Cpu), CpuMax = _minute.Max(x => x.Cpu),
                Mem = _minute.Average(x => x.MemPercent), Gpu = _minute.Average(x => x.Gpu),
                Disk = _minute.Average(x => x.DiskActive),
                Down = _minute.Average(x => x.NetDown), Up = _minute.Average(x => x.NetUp),
                Temp = temps.Count > 0 ? temps.Max() : null,
            };
            _store.Append(Path.Combine("history", $"metrics-{DateTime.UtcNow:yyyyMM}.jsonl"), stat);
            _minute.Clear();
            _minuteOf = minute;
        }
        _minute.Add(s);
    }

    public List<LiveSample> Recent(int count)
    {
        count = Math.Clamp(count, 1, RingSize);
        var list = new List<LiveSample>(count);
        lock (_gate)
        {
            for (int i = count; i >= 1; i--)
            {
                var item = _ring[((_head - i) % RingSize + RingSize) % RingSize];
                if (item is not null) list.Add(item);
            }
        }
        return list;
    }

    public List<MinuteStat> History(double hours)
    {
        long from = DateTimeOffset.UtcNow.AddHours(-hours).ToUnixTimeMilliseconds();
        var result = new List<MinuteStat>();
        var months = new[] { DateTime.UtcNow.AddMonths(-1), DateTime.UtcNow };
        foreach (var m in months)
            result.AddRange(_store.ReadLines<MinuteStat>(Path.Combine("history", $"metrics-{m:yyyyMM}.jsonl")).Where(x => x.T >= from));
        return result;
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("metrics.live", c =>
        {
            Touch();
            var recent = Recent(c.Int("history", 60));
            return new
            {
                now = Latest,
                history = new
                {
                    t = recent.Select(x => x.T),
                    cpu = recent.Select(x => x.Cpu),
                    mem = recent.Select(x => x.MemPercent),
                    gpu = recent.Select(x => x.Gpu),
                    disk = recent.Select(x => x.DiskActive),
                    read = recent.Select(x => x.DiskRead),
                    write = recent.Select(x => x.DiskWrite),
                    down = recent.Select(x => x.NetDown),
                    up = recent.Select(x => x.NetUp),
                    temp = recent.Select(x => x.Temp ?? 0),
                    mhz = recent.Select(x => x.CpuMhz),
                },
            };
        });
        rpc.On("metrics.history", c => History(c.Dbl("hours", 24)));
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _pdh.Dispose();
    }
}
