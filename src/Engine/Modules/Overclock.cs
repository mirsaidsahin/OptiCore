using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

/// <summary>
/// Overclocking assistant. OptiCore does not write CPU/GPU clocks or voltages itself: that needs a
/// kernel driver, and a wrong value can damage hardware or corrupt data. Instead it finds out what this
/// machine can be overclocked with, points to the vendor's official tool, spots memory running below its
/// rated speed (XMP/EXPO off), tunes Windows' own processor boost policy (reversible), and verifies the
/// result with the stability test.
/// </summary>
public sealed partial class OverclockService
{
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid MaxState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid MinState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");

    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);

    private readonly SystemInfoService _system;
    private readonly AppsService _apps;
    private readonly ChangeJournal _journal;

    public OverclockService(SystemInfoService system, AppsService apps, ChangeJournal journal)
    {
        _system = system; _apps = apps; _journal = journal;
        journal.RegisterUndo("power.value", e =>
        {
            var scheme = Guid.Parse(e.Before["scheme"]!);
            Write(scheme, Setting(e.Before["setting"]!), e.Before["power"] == "dc", uint.Parse(e.Before["value"]!));
        });
    }

    // ------------------------------------------------------------------ detection
    public sealed record CpuVerdict(string Status, LText Reason, string? Tool);

    [GeneratedRegex(@"\b(i[3579]|Core\(TM\) i[3579])-\d{4,5}(K|KF|KS)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelK();
    [GeneratedRegex(@"Core.*Ultra \d+ \d{3}K", RegexOptions.IgnoreCase)]
    private static partial Regex IntelUltraK();
    [GeneratedRegex(@"\b(i[3579]|Core\(TM\) i[3579])-\d{4,5}HX\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelHX();
    [GeneratedRegex(@"\bi9-\d{4,5}X(E)?\b|\bi7-\d{4}X\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelX();
    [GeneratedRegex(@"Ryzen.*\d{4}\s?(U|H|HS|HX|HX3D)\b|Ryzen AI", RegexOptions.IgnoreCase)]
    private static partial Regex RyzenMobile();

    /// <summary>Classifies a CPU by its model name: unlocked | limited | locked.</summary>
    public static CpuVerdict ClassifyCpu(string name, bool laptop)
    {
        if (IntelK().IsMatch(name) || IntelUltraK().IsMatch(name) || IntelX().IsMatch(name))
            return new("unlocked", Loc.T("Çarpanı açık bir Intel işlemci: çekirdek hızı, önbellek hızı ve güç sınırları ayarlanabilir.", "An unlocked Intel CPU: core clock, cache clock and power limits can be tuned."), "xtu");
        if (IntelHX().IsMatch(name))
            return new("limited", Loc.T("HX serisi dizüstü işlemcileri kısmen açıktır; ne kadar ayar yapılabileceğini dizüstünün BIOS'u belirler.", "HX-series laptop CPUs are partly unlocked; how much can be tuned depends on the laptop's BIOS."), "xtu");
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return new("locked", Loc.T("Bu Intel işlemcinin çarpanı kilitli. Hız aşırtma yapılamaz; yalnızca Windows'un turbo ayarları ve soğutma performansı etkiler.", "This Intel CPU is multiplier-locked. It cannot be overclocked; only Windows' boost settings and cooling affect performance."), null);
        if (name.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) || name.Contains("Threadripper", StringComparison.OrdinalIgnoreCase))
        {
            if (RyzenMobile().IsMatch(name) || laptop)
                return new("locked", Loc.T("Dizüstü Ryzen işlemcileri hız aşırtmayı desteklemez; Ryzen Master da bunları desteklemez.", "Laptop Ryzen CPUs do not support overclocking; Ryzen Master does not support them either."), null);
            if (name.Contains("X3D", StringComparison.OrdinalIgnoreCase))
                return new("limited", Loc.T("X3D işlemcilerde çarpan ayarı kısıtlıdır; Precision Boost Overdrive ve Curve Optimizer (negatif voltaj eğrisi) kullanılabilir.", "X3D CPUs have restricted multiplier control; Precision Boost Overdrive and Curve Optimizer (negative voltage curve) are available."), "ryzenmaster");
            return new("unlocked", Loc.T("Masaüstü Ryzen işlemcilerinin hepsi açıktır. En güvenli ve etkili yol Precision Boost Overdrive (PBO) ve Curve Optimizer'dır; sabit çarpan genellikle tek çekirdek performansını düşürür.", "Every desktop Ryzen CPU is unlocked. The safest and most effective route is Precision Boost Overdrive (PBO) with Curve Optimizer; a fixed multiplier usually lowers single-core performance."), "ryzenmaster");
        }
        return new("unknown", Loc.T("Bu işlemci için hız aşırtma desteği belirlenemedi.", "Overclocking support could not be determined for this CPU."), null);
    }

    private static readonly Dictionary<string, (string Name, string Url, string Match)> Tools = new()
    {
        ["xtu"] = ("Intel Extreme Tuning Utility (XTU)", "https://www.intel.com/content/www/us/en/download/17881/intel-extreme-tuning-utility-intel-xtu.html", "Extreme Tuning"),
        ["ryzenmaster"] = ("AMD Ryzen Master", "https://www.amd.com/en/products/software/ryzen-master.html", "Ryzen Master"),
        ["afterburner"] = ("MSI Afterburner", "https://www.msi.com/Landing/afterburner/graphics-cards", "Afterburner"),
        ["nvidiaapp"] = ("NVIDIA App", "https://www.nvidia.com/en-us/software/nvidia-app/", "NVIDIA app"),
        ["adrenalin"] = ("AMD Software: Adrenalin Edition", "https://www.amd.com/en/products/software/adrenalin.html", "AMD Software"),
        ["intelgfx"] = ("Intel Graphics Software", "https://www.intel.com/content/www/us/en/download/785597/intel-arc-iris-xe-graphics-windows.html", "Intel® Graphics Software"),
    };

    private object Tool(string id)
    {
        var (name, url, match) = Tools[id];
        bool installed = _apps.List().Any(a => a.Name.Contains(match, StringComparison.OrdinalIgnoreCase));
        return new { id, name, url, installed };
    }

    private object Gpu(GpuInfo g)
    {
        string n = g.Name;
        (string status, LText reason, string[] tools) = n switch
        {
            _ when n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase) =>
                ("unlocked", Loc.T("NVIDIA ekran kartlarında çekirdek ve bellek saati yükseltilebilir. NVIDIA App'teki \"Otomatik ayarlama\" kartı kendisi tarar ve güvenli bir hız aşırtma bulur; elle ayar için MSI Afterburner kullanılır.",
                    "NVIDIA cards can have their core and memory clocks raised. \"Automatic tuning\" in the NVIDIA App scans the card and finds a safe overclock by itself; MSI Afterburner is used for manual tuning."), new[] { "nvidiaapp", "afterburner" }),
            _ when n.Contains("Radeon", StringComparison.OrdinalIgnoreCase) && !g.Integrated =>
                ("unlocked", Loc.T("AMD Radeon kartlarında Adrenalin'in \"Performans > Ayarlama\" bölümü otomatik hız aşırtma, voltaj düşürme ve güç sınırı ayarı sunar.", "On AMD Radeon cards Adrenalin's \"Performance > Tuning\" offers automatic overclocking, undervolting and power limit control."), new[] { "adrenalin", "afterburner" }),
            _ when n.Contains("Arc", StringComparison.OrdinalIgnoreCase) && !g.Integrated =>
                ("unlocked", Loc.T("Intel Arc kartlarında Intel Graphics Software'in performans ayarı bölümü frekans kaydırma ve güç sınırı ayarı sunar.", "On Intel Arc cards the performance tuning section of Intel Graphics Software offers frequency offset and power limit control."), new[] { "intelgfx" }),
            _ when g.Integrated =>
                ("locked", Loc.T("Tümleşik ekran kartı: frekansı işlemciyle birlikte yönetilir ve ayrıca hız aşırtılamaz. En büyük fark çift kanallı bellekle (iki bellek modülü) elde edilir.", "Integrated graphics: its frequency is managed together with the CPU and cannot be overclocked separately. The biggest difference comes from dual-channel memory (two memory modules)."), Array.Empty<string>()),
            _ => ("unknown", Loc.T("Bu ekran kartı için hız aşırtma yolu belirlenemedi.", "No overclocking route could be determined for this graphics card."), Array.Empty<string>()),
        };
        return new { name = n, status, reason, integrated = g.Integrated, tools = tools.Select(Tool).ToList() };
    }

    /// <summary>Memory runs below its rated speed when XMP/EXPO is off in the BIOS (very common on new desktops).</summary>
    internal static (bool Below, int Rated, int Configured) MemorySpeed(MemInfo mem)
    {
        var m = mem.Modules.Where(x => x.SpeedMhz > 0 && x.ConfiguredMhz > 0).ToList();
        if (m.Count == 0) return (false, 0, 0);
        int rated = m.Min(x => x.SpeedMhz), configured = m.Min(x => x.ConfiguredMhz);
        return (configured < rated * 0.95, rated, configured);
    }

    // ------------------------------------------------------------------ Windows processor power policy
    private static Guid Setting(string id) => id switch { "boost" => BoostMode, "max" => MaxState, "min" => MinState, _ => throw RpcException.Invalid("setting") };

    private static uint? Read(Guid scheme, Guid setting, bool dc)
    {
        var sub = SubProcessor;
        uint rc = dc ? PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint v) : PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v);
        return rc == 0 ? v : null;
    }

    private static void Write(Guid scheme, Guid setting, bool dc, uint value)
    {
        var sub = SubProcessor;
        uint rc = dc ? PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value) : PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        if (rc == 5) throw new UnauthorizedAccessException("power setting");
        if (rc != 0) throw new InvalidOperationException("PowerWrite failed: " + rc);
        PowerPlans.SetActive(scheme); // re-apply so the change takes effect immediately
    }

    private object Policy()
    {
        var scheme = PowerPlans.Active() ?? throw RpcException.Failed("No active power plan");
        object Pair(Guid s) => new { ac = Read(scheme, s, false), dc = Read(scheme, s, true) };
        return new { scheme, boost = Pair(BoostMode), max = Pair(MaxState), min = Pair(MinState) };
    }

    private object SetPolicy(string setting, string power, uint value)
    {
        if (power is not ("ac" or "dc")) throw RpcException.Invalid("power");
        if (setting == "boost" && value > 6) throw RpcException.Invalid("boost mode");
        if (setting is "max" or "min" && value is < 5 or > 100) throw RpcException.Invalid("percent");
        var scheme = PowerPlans.Active() ?? throw RpcException.Failed("No active power plan");
        var guid = Setting(setting);
        bool dc = power == "dc";
        uint? before = Read(scheme, guid, dc);
        LText what = setting switch { "boost" => Loc.T("İşlemci turbo modu", "Processor boost mode"), "max" => Loc.T("En yüksek işlemci durumu", "Maximum processor state"), _ => Loc.T("En düşük işlemci durumu", "Minimum processor state") };
        using var scope = _journal.Begin("overclock", Loc.T("{0} ({1}): {2}", "{0} ({1}): {2}").F(what, dc ? Loc.S("pil", "battery") : Loc.S("priz", "plugged in"), value));
        var entry = scope.Custom("power.value", $"{setting}/{power}",
            new() { ["scheme"] = scheme.ToString(), ["setting"] = setting, ["power"] = power, ["value"] = before?.ToString() },
            new() { ["value"] = value.ToString() },
            () => Write(scheme, guid, dc, value), reversible: before is not null);
        scope.Commit();
        if (entry.Error == "ELEVATION_REQUIRED") throw RpcException.Elevation();
        if (entry.Error is not null) throw RpcException.Failed(entry.Error);
        return new { simulated = scope.DryRun };
    }

    public IEnumerable<Finding> Findings()
    {
        var (below, rated, configured) = MemorySpeed(_system.Hardware().Memory);
        if (below && _system.Hardware().Computer.Type != "laptop")
            yield return new Finding
            {
                Id = "overclock.xmp", Group = "performance", Severity = Severity.Warning, Penalty = 8,
                Title = Loc.T("Bellek nominal hızının altında çalışıyor", "Memory runs below its rated speed"),
                Detail = Loc.T("Bellek modülleriniz daha yüksek bir hız için üretilmiş ama BIOS'ta XMP (Intel) ya da EXPO (AMD) profili açılmamış. Bu, masaüstü bilgisayarlarda en sık atlanan ve en güvenli performans kazancıdır.",
                               "Your memory modules are rated for a higher speed, but the XMP (Intel) or EXPO (AMD) profile is not enabled in the BIOS. This is the most commonly missed and safest performance gain on desktops."),
                Impact = Loc.T("İşlemciye bağlı oyunlarda ve tümleşik grafikte belirgin FPS artışı.", "A clear FPS gain in CPU-bound games and on integrated graphics."),
            }.Ev(Loc.T("Nominal / çalışan", "Rated / running"), $"{rated} / {configured} MHz").Go(Loc.T("Hız aşırtma sayfası", "Overclocking page"), "overclock");
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("oc.status", _ =>
        {
            var hw = _system.Hardware();
            bool laptop = hw.Computer.Type == "laptop";
            var cpu = ClassifyCpu(hw.Cpu.Name, laptop);
            var (below, rated, configured) = MemorySpeed(hw.Memory);
            return new
            {
                laptop, elevated = Elevation.IsElevated,
                cpu = new { hw.Cpu.Name, cpu.Status, cpu.Reason, tool = cpu.Tool is null ? null : Tool(cpu.Tool), hw.Cpu.BaseMhz, hw.Cpu.Cores, hw.Cpu.Threads },
                gpus = hw.Gpus.Select(Gpu).ToList(),
                memory = new { below, rated, configured, type = hw.Memory.Modules.FirstOrDefault()?.Type, modules = hw.Memory.Modules.Count, slots = hw.Memory.SlotsTotal },
                board = new { hw.Board.Manufacturer, hw.Board.Product, hw.Board.BiosVersion },
                policy = Policy(),
            };
        });
        rpc.On("oc.setPolicy", c => SetPolicy(c.Str("setting"), c.Str("power"), (uint)Math.Max(0, c.Int("value"))));
    }
}
