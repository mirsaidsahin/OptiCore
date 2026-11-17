using System.Text.RegularExpressions;
using OptiCore.Engine.Core;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

/// <summary>
/// "How good is this PC?" Places each component against well-known hardware using an embedded table of
/// approximate performance figures compiled from widely published review averages (Cinebench R23 for
/// processors, 1080p game averages for graphics cards). The table ships with the app: nothing is fetched
/// from or sent to benchmark websites. Where OptiCore has measured the component itself, the measurement
/// wins over the table. Figures are approximations (about ±15%), and the page says so.
/// </summary>
public static partial class HardwareRank
{
    public sealed record Ref(string Name, double Value, bool Mobile = false);

    // Cinebench R23 multi-core and single-core (approximate, stock settings).
    private static readonly (string Name, int Multi, int Single, bool Mobile)[] Cpus =
    [
        ("Intel Core i3-10100", 4900, 1200, false), ("Intel Core i3-12100", 8700, 1700, false), ("Intel Core i3-13100", 9000, 1800, false),
        ("Intel Core i5-10400", 8500, 1150, false), ("Intel Core i5-11400", 10000, 1450, false), ("Intel Core i5-12400", 12000, 1700, false),
        ("Intel Core i5-12600K", 17500, 1900, false), ("Intel Core i5-13400", 15500, 1750, false), ("Intel Core i5-13600K", 24000, 2000, false),
        ("Intel Core i5-14400", 16500, 1800, false), ("Intel Core i5-14600K", 24500, 2100, false),
        ("Intel Core i7-10700K", 13500, 1300, false), ("Intel Core i7-12700K", 22500, 1950, false), ("Intel Core i7-13700K", 30000, 2100, false),
        ("Intel Core i7-14700K", 35000, 2150, false), ("Intel Core i9-12900K", 27000, 2000, false), ("Intel Core i9-13900K", 39000, 2250, false),
        ("Intel Core i9-14900K", 40000, 2300, false), ("Intel Core Ultra 5 245K", 23000, 2100, false), ("Intel Core Ultra 7 265K", 35000, 2200, false),
        ("Intel Core Ultra 9 285K", 42000, 2300, false),
        ("Intel Core i3-1115G4", 3600, 1250, true), ("Intel Core i5-1135G7", 5500, 1350, true), ("Intel Core i5-1235U", 8000, 1600, true),
        ("Intel Core i7-1255U", 9000, 1700, true), ("Intel Core i5-1335U", 8500, 1700, true), ("Intel Core i7-1360P", 10500, 1800, true),
        ("Intel Core i5-12450H", 10500, 1600, true), ("Intel Core i7-12700H", 16000, 1800, true), ("Intel Core i7-13700H", 17000, 1850, true),
        ("Intel Core i9-13980HX", 30000, 2050, true), ("Intel Core Ultra 7 155H", 16000, 1750, true), ("Intel Core Ultra 7 258V", 10500, 1900, true),
        ("AMD Ryzen 5 3600", 9000, 1250, false), ("AMD Ryzen 5 5600", 11000, 1550, false), ("AMD Ryzen 5 5600X", 11300, 1600, false),
        ("AMD Ryzen 7 5700X", 14500, 1550, false), ("AMD Ryzen 7 5800X", 15200, 1600, false), ("AMD Ryzen 7 5800X3D", 14500, 1480, false),
        ("AMD Ryzen 9 5900X", 21500, 1650, false), ("AMD Ryzen 9 5950X", 26500, 1650, false), ("AMD Ryzen 5 7600", 14500, 1850, false),
        ("AMD Ryzen 5 7600X", 15000, 1900, false), ("AMD Ryzen 7 7700X", 19500, 1950, false), ("AMD Ryzen 7 7800X3D", 18000, 1800, false),
        ("AMD Ryzen 9 7900X", 29000, 2000, false), ("AMD Ryzen 9 7950X", 38000, 2050, false), ("AMD Ryzen 5 9600X", 17000, 2150, false),
        ("AMD Ryzen 7 9700X", 20500, 2200, false), ("AMD Ryzen 7 9800X3D", 23000, 2100, false), ("AMD Ryzen 9 9950X", 42000, 2250, false),
        ("AMD Ryzen 5 5500U", 7500, 1150, true), ("AMD Ryzen 7 5700U", 9000, 1250, true), ("AMD Ryzen 7 5800H", 12500, 1450, true),
        ("AMD Ryzen 5 7530U", 8500, 1400, true), ("AMD Ryzen 7 7840HS", 16500, 1750, true), ("AMD Ryzen 9 7945HX", 32000, 1950, true),
        ("AMD Ryzen AI 9 HX 370", 20000, 1950, true),
    ];

    // Relative 1080p gaming performance, GeForce RTX 3060 = 100.
    private static readonly (string Name, int Index, bool Mobile)[] Gpus =
    [
        ("GeForce GTX 1050 Ti", 30, false), ("GeForce GTX 1060", 55, false), ("GeForce GTX 1070", 70, false), ("GeForce GTX 1080", 85, false),
        ("GeForce GTX 1080 Ti", 105, false), ("GeForce GTX 1650", 45, false), ("GeForce GTX 1650 SUPER", 55, false), ("GeForce GTX 1660", 60, false),
        ("GeForce GTX 1660 SUPER", 68, false), ("GeForce GTX 1660 Ti", 68, false), ("GeForce RTX 2060", 80, false), ("GeForce RTX 2060 SUPER", 92, false),
        ("GeForce RTX 2070", 98, false), ("GeForce RTX 2070 SUPER", 110, false), ("GeForce RTX 2080", 120, false), ("GeForce RTX 2080 SUPER", 125, false),
        ("GeForce RTX 2080 Ti", 150, false), ("GeForce RTX 3050", 70, false), ("GeForce RTX 3060", 100, false), ("GeForce RTX 3060 Ti", 122, false),
        ("GeForce RTX 3070", 135, false), ("GeForce RTX 3070 Ti", 142, false), ("GeForce RTX 3080", 175, false), ("GeForce RTX 3080 Ti", 190, false),
        ("GeForce RTX 3090", 195, false), ("GeForce RTX 3090 Ti", 210, false), ("GeForce RTX 4060", 115, false), ("GeForce RTX 4060 Ti", 135, false),
        ("GeForce RTX 4070", 175, false), ("GeForce RTX 4070 SUPER", 200, false), ("GeForce RTX 4070 Ti", 210, false), ("GeForce RTX 4070 Ti SUPER", 225, false),
        ("GeForce RTX 4080", 255, false), ("GeForce RTX 4080 SUPER", 260, false), ("GeForce RTX 4090", 325, false), ("GeForce RTX 5060", 140, false),
        ("GeForce RTX 5060 Ti", 160, false), ("GeForce RTX 5070", 210, false), ("GeForce RTX 5070 Ti", 255, false), ("GeForce RTX 5080", 290, false),
        ("GeForce RTX 5090", 400, false),
        ("GeForce MX450", 22, true), ("GeForce RTX 2050", 35, true), ("GeForce RTX 3050 Laptop GPU", 50, true), ("GeForce RTX 3060 Laptop GPU", 85, true),
        ("GeForce RTX 3070 Laptop GPU", 110, true), ("GeForce RTX 4050 Laptop GPU", 80, true), ("GeForce RTX 4060 Laptop GPU", 95, true),
        ("GeForce RTX 4070 Laptop GPU", 110, true), ("GeForce RTX 4080 Laptop GPU", 170, true), ("GeForce RTX 4090 Laptop GPU", 200, true),
        ("Radeon RX 570", 42, false), ("Radeon RX 580", 50, false), ("Radeon RX 5500 XT", 50, false), ("Radeon RX 5600 XT", 72, false),
        ("Radeon RX 5700 XT", 90, false), ("Radeon RX 6600", 95, false), ("Radeon RX 6600 XT", 108, false), ("Radeon RX 6650 XT", 112, false),
        ("Radeon RX 6700 XT", 135, false), ("Radeon RX 6750 XT", 140, false), ("Radeon RX 6800", 170, false), ("Radeon RX 6800 XT", 195, false),
        ("Radeon RX 6900 XT", 205, false), ("Radeon RX 6950 XT", 220, false), ("Radeon RX 7600", 105, false), ("Radeon RX 7600 XT", 110, false),
        ("Radeon RX 7700 XT", 165, false), ("Radeon RX 7800 XT", 195, false), ("Radeon RX 7900 GRE", 210, false), ("Radeon RX 7900 XT", 250, false),
        ("Radeon RX 7900 XTX", 285, false), ("Radeon RX 9060 XT", 140, false), ("Radeon RX 9070", 230, false), ("Radeon RX 9070 XT", 260, false),
        ("Arc A380", 40, false), ("Arc A750", 95, false), ("Arc A770", 105, false), ("Arc B580", 115, false),
        ("Intel UHD Graphics 620", 6, true), ("Intel UHD Graphics 770", 9, false), ("Intel Iris Xe Graphics", 18, true), ("Intel Arc Graphics", 30, true),
        ("Intel Arc 140V", 35, true), ("Radeon Vega 8", 13, true), ("Radeon 680M", 28, true), ("Radeon 780M", 33, true), ("Radeon 890M", 40, true),
    ];

    [GeneratedRegex(@"\(R\)|\(TM\)|®|™|\d+th Gen |\d+-Core Processor|w/ Radeon.*|with Radeon.*|CPU @.*|Processor|NVIDIA |AMD |Intel\(R\) ", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    /// <summary>Normalizes a model name so "12th Gen Intel(R) Core(TM) i5-1235U" matches "Intel Core i5-1235U".</summary>
    internal static string Key(string name)
    {
        string s = Noise().Replace(name, " ");
        s = Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
        return s.Replace("intel ", "").Replace("core ", "").Replace("geforce ", "").Replace("radeon ", "").Replace("graphics", "").Replace("  ", " ").Trim();
    }

    private static T? Find<T>(IEnumerable<T> table, Func<T, string> name, string model) where T : struct
    {
        string key = Key(model);
        // Longest matching name wins, so "RTX 4070 Ti SUPER" is not taken for "RTX 4070".
        foreach (var row in table.OrderByDescending(r => name(r).Length))
        {
            string k = Key(name(row));
            if (key == k || (k.Length >= 6 && Regex.IsMatch(key, @"(^|\s)" + Regex.Escape(k) + @"($|\s)"))) return row;
        }
        return null;
    }

    public static (int Multi, int Single, bool Mobile)? Cpu(string model) =>
        Find(Cpus, c => c.Name, model) is { } c ? (c.Multi, c.Single, c.Mobile) : null;

    public static int? Gpu(string model)
    {
        bool laptop = model.Contains("Laptop", StringComparison.OrdinalIgnoreCase);
        var rows = Gpus.Where(g => !laptop || g.Mobile || !g.Name.Contains("RTX")).ToArray();
        return Find(rows, g => g.Name, model)?.Index;
    }

    /// <summary>OptiCore CPU score estimated from the table (same 40/60 weighting as the CPU benchmark, i5-1235U = 1000).</summary>
    public static double CpuScore(int multi, int single) => 1000 * (0.4 * single / 1600.0 + 0.6 * multi / 8000.0);

    public static int Tier(double value, double[] limits) => limits.Count(l => value >= l) + 1; // 1..5

    private static readonly LText[] Tiers =
    [
        Loc.T("Giriş", "Entry"), Loc.T("Orta", "Mid-range"), Loc.T("İyi", "Good"), Loc.T("Üst", "High-end"), Loc.T("Amiral", "Flagship"),
    ];

    private static object Component(string id, string name, int tier, double value, string unit, LText verdict, IEnumerable<Ref> refs, string source, string? search) => new
    {
        id, name, tier, tierLabel = Tiers[tier - 1], value = Math.Round(value), unit, verdict, source,
        refs = refs.Where(r => Key(r.Name) != Key(name)).Select(r => new { r.Name, value = Math.Round(r.Value), r.Mobile }).ToList(),
        search = search is null ? null : "https://www.bing.com/search?q=" + Uri.EscapeDataString(search),
    };

    public static object Evaluate(Hardware hw, List<BenchResult> benches)
    {
        var parts = new List<object>();
        var tiers = new List<(int Tier, double Weight)>();
        int? cpuTier = null, gpuTier = null;
        bool laptop = hw.Computer.Type == "laptop";

        // ---- processor
        var measuredCpu = benches.Where(b => b.Kind == "cpu").OrderByDescending(b => b.Time).FirstOrDefault();
        var cpuRow = Cpu(hw.Cpu.Name);
        double? cpuScore = measuredCpu?.Score ?? (cpuRow is { } r ? CpuScore(r.Multi, r.Single) : null);
        if (cpuScore is double cs)
        {
            int tier = Tier(cs, [900, 1500, 2300, 3300]);
            var refs = new[] { "Intel Core i3-12100", "Intel Core i5-12400", "AMD Ryzen 5 5600X", "Intel Core i5-13600K", "AMD Ryzen 7 7800X3D", "Intel Core i7-14700K", "AMD Ryzen 9 9950X", "Intel Core i5-1135G7", "Intel Core i7-12700H" }
                .Select(n => Cpus.First(c => c.Name == n)).Select(c => new Ref(c.Name, CpuScore(c.Multi, c.Single), c.Mobile));
            LText verdict = tier switch
            {
                1 => Loc.T("Ofis, internet ve okul işleri için yeterli; oyunlarda ve ağır işlerde zorlanır.", "Enough for office, web and school work; struggles with games and heavy work."),
                2 => Loc.T("Günlük işler için rahat; oyunlarda çoğu zaman ekran kartından önce sınıra gelmez ama yoğun simülasyonlarda (ETS2 şehirleri, MSFS) yetersiz kalabilir.", "Comfortable for daily work; in games it is usually not the first limit, but heavy simulations may outgrow it."),
                3 => Loc.T("Oyun ve içerik üretimi için iyi; çoğu oyunda ekran kartını tam besler.", "Good for gaming and content creation; it keeps the graphics card fed in most games."),
                4 => Loc.T("Üst sınıf: yüksek kare hızlarında oyun ve yayın aynı anda rahat.", "High-end: gaming at high frame rates while streaming is easy."),
                _ => Loc.T("Amiral sınıfı: bugünün en hızlı işlemcileri arasında.", "Flagship class: among today's fastest processors."),
            };
            cpuTier = tier;
            parts.Add(Component("cpu", hw.Cpu.Name, tier, cs, "puan", verdict, refs, measuredCpu is not null ? "measured" : "table", hw.Cpu.Name + " benchmark"));
            tiers.Add((tier, 0.35));
        }
        else parts.Add(new { id = "cpu", name = hw.Cpu.Name, tier = 0, unknown = true, verdict = Loc.T("Bu işlemci tabloda yok; puanı görmek için Benchmark'ta işlemci testini çalıştırın.", "This processor is not in the table; run the CPU test in Benchmark to see its score."), search = "https://www.bing.com/search?q=" + Uri.EscapeDataString(hw.Cpu.Name + " benchmark") });

        // ---- graphics: the strongest adapter counts
        var gpus = hw.Gpus.Select(g => (g.Name, Index: Gpu(g.Name))).OrderByDescending(g => g.Index ?? -1).ToList();
        var best = gpus.FirstOrDefault();
        if (best.Index is int gi)
        {
            int tier = Tier(gi, [40, 90, 150, 240]);
            var refs = new[] { "Intel Iris Xe Graphics", "GeForce GTX 1650", "GeForce RTX 3060", "Radeon RX 7600", "GeForce RTX 4060 Ti", "GeForce RTX 4070 SUPER", "Radeon RX 7900 XTX", "GeForce RTX 4090" }
                .Select(n => Gpus.First(g => g.Name == n)).Select(g => new Ref(g.Name, g.Index, g.Mobile));
            LText verdict = tier switch
            {
                1 => Loc.T("Video, internet ve hafif oyunlar (Roblox, Minecraft, eski oyunlar) için; yeni oyunlarda 720p düşük ayarlar gerekir.", "For video, web and light games (Roblox, Minecraft, older titles); new games need 720p low settings."),
                2 => Loc.T("1080p'de çoğu oyun orta ayarlarda akıcı.", "Most games run smoothly at 1080p medium."),
                3 => Loc.T("1080p yüksek ayarlarda ve 1440p'de rahat oyun.", "Comfortable gaming at 1080p high and 1440p."),
                4 => Loc.T("1440p yüksek ayarlar ve ışın izleme için güçlü.", "Strong for 1440p high settings and ray tracing."),
                _ => Loc.T("4K ve en yüksek ayarlar için amiral sınıfı.", "Flagship class for 4K and maximum settings."),
            };
            gpuTier = tier;
            parts.Add(Component("gpu", best.Name, tier, gi, "RTX 3060 = 100", verdict, refs, "table", best.Name + " benchmark"));
            tiers.Add((tier, 0.35));
        }
        else if (best.Name is not null)
            parts.Add(new { id = "gpu", name = best.Name, tier = 0, unknown = true, verdict = Loc.T("Bu ekran kartı tabloda yok.", "This graphics card is not in the table."), search = "https://www.bing.com/search?q=" + Uri.EscapeDataString(best.Name + " benchmark") });

        // ---- memory: capacity matters most, then generation
        double gb = hw.Memory.InstalledBytes / (double)(1L << 30);
        string type = hw.Memory.Modules.FirstOrDefault()?.Type ?? "";
        int memTier = gb switch { < 7 => 1, < 15 => 2, < 31 => 3, < 63 => 4, _ => 5 };
        if (type is "DDR3" or "LPDDR3" && memTier > 1) memTier--;
        bool single = hw.Memory.Modules.Count == 1 && hw.Memory.SlotsTotal > 1;
        parts.Add(new
        {
            id = "memory", name = $"{Math.Round(gb)} GB {type}".Trim(), tier = memTier, tierLabel = Tiers[memTier - 1], value = Math.Round(gb), unit = "GB", source = "system",
            verdict = (memTier switch
            {
                1 => Loc.T("Windows 11 ve tarayıcılar için az; uygulamalar arasında geçişte takılma olur.", "Too little for Windows 11 and browsers; switching between apps stutters."),
                2 => Loc.T("Günlük işler için yeterli; yeni oyunlar ve çok sekme açıkken sınırda.", "Enough for daily work; tight with new games and many browser tabs."),
                3 => Loc.T("Oyun ve günlük kullanım için bugünün tatlı noktası.", "Today's sweet spot for gaming and daily use."),
                4 => Loc.T("Oyun, yayın ve içerik üretimi için bol.", "Plenty for gaming, streaming and content creation."),
                _ => Loc.T("Profesyonel iş yükleri için.", "For professional workloads."),
            }).ToString() + (single ? " " + Loc.S("Tek modül takılı: ikinci bir eş modül çift kanal çalışmayı açar ve belirgin hız kazandırır.", "One module installed: a matching second module enables dual channel and a noticeable speed-up.") : ""),
        });
        tiers.Add((memTier, 0.15));

        // ---- storage: measured speed if available, otherwise the media type
        var sys = hw.Disks.FirstOrDefault(d => d.IsSystem) ?? hw.Disks.FirstOrDefault();
        var disk = benches.Where(b => b.Kind == "disk").OrderByDescending(b => b.Time).FirstOrDefault();
        double read = disk?.Metrics.GetValueOrDefault("seqReadMBs") ?? (sys?.Media == "HDD" ? 150 : sys?.Bus == "NVMe" ? 3000 : sys?.Media == "SSD" ? 520 : 150);
        int diskTier = read switch { < 300 => 1, < 1000 => 2, < 4000 => 3, < 8000 => 4, _ => 5 };
        parts.Add(new
        {
            id = "storage", name = sys?.Model ?? "?", tier = diskTier, tierLabel = Tiers[diskTier - 1], value = Math.Round(read), unit = "MB/s", source = disk is null ? "type" : "measured",
            verdict = diskTier switch
            {
                1 => Loc.T("Mekanik disk: açılış ve oyun yüklemeleri yavaş. SSD'ye geçmek bu bilgisayarın en etkili yükseltmesidir.", "Mechanical disk: boot and game loading are slow. Moving to an SSD is the most effective upgrade for this PC."),
                2 => Loc.T("SATA SSD: günlük kullanımda hızlı; NVMe ile fark yalnızca büyük dosya kopyalamada hissedilir.", "SATA SSD: fast in daily use; NVMe only feels different when copying large files."),
                3 => Loc.T("NVMe SSD: oyun yüklemeleri ve açılış için çok iyi.", "NVMe SSD: very good for game loading and boot."),
                _ => Loc.T("Yeni nesil NVMe: en hızlı depolama sınıfı.", "Latest-generation NVMe: the fastest storage class."),
            },
            refs = new[] { new Ref("HDD (7200 rpm)", 150), new Ref("SATA SSD", 550), new Ref("NVMe PCIe 3.0", 3500), new Ref("NVMe PCIe 4.0", 7000), new Ref("NVMe PCIe 5.0", 12000) }
                .Select(x => new { x.Name, value = x.Value, x.Mobile }).ToList(),
        });
        tiers.Add((diskTier, 0.15));

        double overall = tiers.Sum(t => t.Tier * t.Weight) / tiers.Sum(t => t.Weight);
        int overallTier = Math.Clamp((int)Math.Round(overall), 1, 5);
        // The weakest of processor and graphics decides what games are playable.
        int gaming = Math.Min(cpuTier ?? overallTier, gpuTier ?? overallTier);
        return new
        {
            overallTier, overallLabel = Tiers[overallTier - 1], laptop, parts,
            gaming = gaming switch
            {
                1 => Loc.T("Hafif oyunlar (Roblox, Minecraft, CS2 düşük ayar) ve eski oyunlar.", "Light games (Roblox, Minecraft, CS2 on low) and older titles."),
                2 => Loc.T("Popüler oyunlar 1080p düşük-orta ayarlarda.", "Popular games at 1080p low to medium."),
                3 => Loc.T("Yeni oyunlar 1080p yüksek ayarlarda.", "New games at 1080p high."),
                4 => Loc.T("Yeni oyunlar 1440p yüksek ayarlarda.", "New games at 1440p high."),
                _ => Loc.T("Her oyun, 4K'ya kadar.", "Every game, up to 4K."),
            },
            bottleneck = cpuTier is int ct && gpuTier is int gt && Math.Abs(ct - gt) >= 2
                ? (ct < gt ? Loc.T("İşlemci, ekran kartının gerisinde: oyunlarda işlemci darboğazı olası.", "The processor lags behind the graphics card: a CPU bottleneck in games is likely.")
                                                   : Loc.T("Ekran kartı, işlemcinin gerisinde: oyunlarda sınırı ekran kartı belirler; en etkili yükseltme ekran kartıdır.", "The graphics card lags behind the processor: it sets the limit in games and is the most effective upgrade."))
                : (LText?)null,
        };
    }
}
