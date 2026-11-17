using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using OptiCore.Engine.Core;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public sealed class BenchResult
{
    public string Id { get; set; } = "";
    public DateTime Time { get; set; }
    /// <summary>cpu | memory | disk | gpu | network | stability</summary>
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Headline score (OptiCore units, higher is better). Only comparable with other OptiCore results.</summary>
    public double Score { get; set; }
    public Dictionary<string, double> Metrics { get; set; } = [];
    public Dictionary<string, string> Info { get; set; } = [];
    public LText? Note { get; set; }
}

/// <summary>
/// Hardware benchmarks with fixed workloads, so results can be compared before and after a change.
/// Scores are relative to a reference: 1000 = a 2022 mid-range laptop (Core i5-1235U, DDR4-3200, PCIe 4 NVMe).
/// </summary>
public sealed class BenchmarkService(JobManager jobs, DataStore store, MetricsSampler metrics, SystemInfoService system)
{
    private const string HistoryFile = "benchmarks/history.jsonl";

    private BenchResult Save(BenchResult r)
    {
        r.Id = Guid.NewGuid().ToString("N")[..10];
        r.Time = DateTime.UtcNow;
        store.Append(HistoryFile, r);
        return r;
    }

    private static void Settle(JobContext job)
    {
        // Let background activity triggered by starting the test die down.
        job.Say(Loc.T("Sistemin sakinleşmesi bekleniyor…", "Waiting for the system to settle…"));
        Thread.Sleep(1500);
    }

    // ------------------------------------------------------------------ CPU
    private static long CpuWork(TimeSpan duration, CancellationToken ct)
    {
        // Mixed integer workload: SHA-256 (crypto units), a prime sieve (branches and memory) and LCG math (ALU).
        var data = new byte[64 * 1024];
        new Random(42).NextBytes(data);
        Span<byte> hash = stackalloc byte[32];
        var sieve = new bool[200_000];
        long ops = 0;
        var sw = Stopwatch.StartNew();
        ulong x = 88172645463325252UL;
        while (sw.Elapsed < duration)
        {
            ct.ThrowIfCancellationRequested();
            SHA256.HashData(data, hash);
            data[0] ^= hash[0];
            Array.Clear(sieve);
            int primes = 0;
            for (int i = 2; i < sieve.Length; i++)
            {
                if (sieve[i]) continue;
                primes++;
                for (long j = (long)i * i; j < sieve.Length; j += i) sieve[j] = true;
            }
            for (int i = 0; i < 200_000; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; }
            ops += primes > 0 ? 1 : 0;
        }
        GC.KeepAlive(x);
        return ops;
    }

    private BenchResult Cpu(JobContext job)
    {
        Settle(job);
        var seconds = TimeSpan.FromSeconds(8);
        job.Report(0.05, Loc.T("Tek çekirdek testi…", "Single-core test…"));
        double single = 0;
        var t = new Thread(() => single = CpuWork(seconds, job.Token) / seconds.TotalSeconds) { Priority = ThreadPriority.AboveNormal };
        t.Start(); t.Join();
        job.ThrowIfCancelled();

        job.Report(0.5, Loc.T("Çok çekirdek testi ({0} iş parçacığı)…", "Multi-core test ({0} threads)…").F(Environment.ProcessorCount));
        long total = 0;
        double mhzSum = 0; int mhzN = 0;
        var threads = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => new Thread(() => Interlocked.Add(ref total, CpuWork(seconds, job.Token)))).ToList();
        threads.ForEach(th => th.Start());
        var sw = Stopwatch.StartNew();
        while (threads.Any(th => th.IsAlive))
        {
            Thread.Sleep(500);
            metrics.Touch();
            if (metrics.Latest is { } s) { mhzSum += s.CpuMhz; mhzN++; }
            job.Report(0.5 + 0.5 * Math.Min(1, sw.Elapsed.TotalSeconds / seconds.TotalSeconds));
        }
        double multi = total / seconds.TotalSeconds;
        const double refSingle = 213, refMulti = 1628; // reference machine iterations per second
        var hw = system.Hardware();
        return Save(new BenchResult
        {
            Kind = "cpu", Label = hw.Cpu.Name,
            Score = Math.Round(1000 * (0.4 * single / refSingle + 0.6 * multi / refMulti)),
            Metrics = new() { ["single"] = Math.Round(1000 * single / refSingle), ["multi"] = Math.Round(1000 * multi / refMulti), ["scaling"] = Math.Round(multi / Math.Max(0.01, single), 2), ["avgMhz"] = mhzN > 0 ? Math.Round(mhzSum / mhzN) : 0 },
            Info = new() { ["threads"] = Environment.ProcessorCount.ToString() },
        });
    }

    // ------------------------------------------------------------------ memory
    private static unsafe BenchResult MemoryBench(JobContext job, Func<BenchResult, BenchResult> save, string label)
    {
        Settle(job);
        const long size = 256L << 20; // well above any CPU cache
        byte* a = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096), b = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
        try
        {
            new Span<byte>(a, (int)Math.Min(int.MaxValue, size)).Fill(1);
            new Span<byte>(b, (int)Math.Min(int.MaxValue, size)).Fill(2);
            job.Report(0.1, Loc.T("Kopyalama bant genişliği…", "Copy bandwidth…"));
            double copy = 0;
            for (int round = 0; round < 3; round++)
            {
                var sw = Stopwatch.StartNew();
                Buffer.MemoryCopy(a, b, size, size);
                copy = Math.Max(copy, size / sw.Elapsed.TotalSeconds);
                job.ThrowIfCancelled();
            }
            job.Report(0.4, Loc.T("Okuma bant genişliği…", "Read bandwidth…"));
            double read = 0;
            for (int round = 0; round < 3; round++)
            {
                var sw = Stopwatch.StartNew();
                ulong sum = 0;
                ulong* p = (ulong*)a;
                long n = size / 8;
                for (long i = 0; i < n; i += 4) sum += p[i] + p[i + 1] + p[i + 2] + p[i + 3];
                read = Math.Max(read, size / sw.Elapsed.TotalSeconds);
                GC.KeepAlive(sum);
            }
            job.Report(0.7, Loc.T("Erişim gecikmesi…", "Access latency…"));
            // Pointer chase through a random cycle: every load depends on the previous one, so caches and prefetchers cannot hide RAM latency.
            const int nodes = 16 << 20; // 128 MB of 8-byte slots
            long* chain = (long*)a;
            var order = Enumerable.Range(0, nodes).ToArray();
            new Random(7).Shuffle(order);
            for (int i = 0; i < nodes; i++) chain[order[i]] = order[(i + 1) % nodes];
            long idx = order[0];
            const int steps = 20_000_000;
            var lat = Stopwatch.StartNew();
            for (int i = 0; i < steps; i++) idx = chain[idx];
            double ns = lat.Elapsed.TotalMilliseconds * 1e6 / steps;
            GC.KeepAlive(idx);
            const double refCopy = 11.3e9, refRead = 9.3e9, refLatency = 233;
            return save(new BenchResult
            {
                Kind = "memory", Label = label,
                Score = Math.Round(1000 * (0.35 * copy / refCopy + 0.35 * read / refRead + 0.3 * refLatency / ns)),
                Metrics = new() { ["copyGBs"] = Math.Round(copy / 1e9, 2), ["readGBs"] = Math.Round(read / 1e9, 2), ["latencyNs"] = Math.Round(ns, 1) },
            });
        }
        finally
        {
            NativeMemory.AlignedFree(a);
            NativeMemory.AlignedFree(b);
        }
    }

    // ------------------------------------------------------------------ disk
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    private static unsafe BenchResult DiskBench(string drive, int sizeMb, JobContext job, Func<BenchResult, BenchResult> save, string label)
    {
        string dir = Path.Combine(drive, "OptiCoreBench");
        if (drive.TrimEnd('\\').Equals(Paths.SystemDrive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) dir = Path.Combine(Path.GetTempPath(), "OptiCoreBench");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "bench.tmp");
        long size = (long)sizeMb << 20;
        var drv = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
        if (drv.AvailableFreeSpace < size * 2) throw RpcException.Failed(Loc.S("Test için yeterli boş alan yok.", "Not enough free space for the test."));
        const int block = 1 << 20, small = 4096;
        byte* buf = (byte*)NativeMemory.AlignedAlloc(block, 4096);
        try
        {
            new Random(3).NextBytes(new Span<byte>(buf, block)); // incompressible data
            Settle(job);
            // Sequential write, unbuffered and write-through: measures the disk, not the RAM cache.
            job.Report(0.05, Loc.T("Sıralı yazma…", "Sequential write…"));
            double seqWrite, seqRead, rndRead, rndWrite;
            using (var h = File.OpenHandle(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None, NoBuffering | FileOptions.WriteThrough, size))
            {
                var sw = Stopwatch.StartNew();
                for (long off = 0; off < size; off += block) { RandomAccess.Write(h, new ReadOnlySpan<byte>(buf, block), off); if ((off & ((64L << 20) - 1)) == 0) job.ThrowIfCancelled(); }
                seqWrite = size / sw.Elapsed.TotalSeconds;
            }
            job.Report(0.3, Loc.T("Sıralı okuma…", "Sequential read…"));
            using (var h = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering))
            {
                var sw = Stopwatch.StartNew();
                for (long off = 0; off < size; off += block) RandomAccess.Read(h, new Span<byte>(buf, block), off);
                seqRead = size / sw.Elapsed.TotalSeconds;
            }
            job.Report(0.55, Loc.T("Rastgele 4K okuma…", "Random 4K read…"));
            var rnd = new Random(11);
            long blocks = size / small;
            using (var h = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering))
            {
                int n = 0;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 5000) { RandomAccess.Read(h, new Span<byte>(buf, small), rnd.NextInt64(blocks) * small); n++; }
                rndRead = n / sw.Elapsed.TotalSeconds;
            }
            job.ThrowIfCancelled();
            job.Report(0.8, Loc.T("Rastgele 4K yazma…", "Random 4K write…"));
            using (var h = File.OpenHandle(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None, NoBuffering | FileOptions.WriteThrough))
            {
                int n = 0;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 5000) { RandomAccess.Write(h, new ReadOnlySpan<byte>(buf, small), rnd.NextInt64(blocks) * small); n++; }
                rndWrite = n / sw.Elapsed.TotalSeconds;
            }
            const double refSeqR = 2.94e9, refSeqW = 1.6e9, refRndR = 8050, refRndW = 20650;
            return save(new BenchResult
            {
                Kind = "disk", Label = label,
                Score = Math.Round(1000 * (0.25 * seqRead / refSeqR + 0.2 * seqWrite / refSeqW + 0.35 * rndRead / refRndR + 0.2 * rndWrite / refRndW)),
                Metrics = new() { ["seqReadMBs"] = Math.Round(seqRead / 1e6), ["seqWriteMBs"] = Math.Round(seqWrite / 1e6), ["rndReadIops"] = Math.Round(rndRead), ["rndWriteIops"] = Math.Round(rndWrite), ["rndReadMBs"] = Math.Round(rndRead * small / 1e6, 1), ["latencyUs"] = Math.Round(1e6 / rndRead) },
                Info = new() { ["drive"] = drv.Name, ["sizeMb"] = sizeMb.ToString() },
                Note = Loc.T("Tek iş parçacıklı, kuyruk derinliği 1: günlük kullanımda hissedilen hız budur. Üreticilerin kutuda yazdığı değerler çok derin kuyruklarla ölçülür ve daha yüksektir.", "Single-threaded at queue depth 1: the speed you feel day to day. Figures on the box are measured with very deep queues and are higher."),
            });
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
            try { File.Delete(file); Directory.Delete(dir); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------ network
    private BenchResult NetworkBench(JobContext job)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OptiCore/" + OptiCoreEngine.Version);
        job.Report(0.05, Loc.T("Gecikme ölçülüyor…", "Measuring latency…"));
        var pings = new List<double>();
        for (int i = 0; i < 8; i++)
        {
            var sw = Stopwatch.StartNew();
            try { using var r = http.Send(new HttpRequestMessage(HttpMethod.Get, "https://speed.cloudflare.com/__down?bytes=0")); pings.Add(sw.Elapsed.TotalMilliseconds); } catch { /* skip */ }
        }
        if (pings.Count == 0) throw RpcException.Failed(Loc.S("Hız testi sunucusuna ulaşılamadı.", "The speed test server could not be reached."));
        job.Report(0.2, Loc.T("İndirme hızı ölçülüyor…", "Measuring download speed…"));
        double down = Transfer(http, true, job);
        job.Report(0.65, Loc.T("Yükleme hızı ölçülüyor…", "Measuring upload speed…"));
        double up = Transfer(http, false, job);
        pings.Sort();
        return Save(new BenchResult
        {
            Kind = "network", Label = "speed.cloudflare.com",
            Score = Math.Round(down * 8 / 1e6),
            Metrics = new() { ["downMbps"] = Math.Round(down * 8 / 1e6, 1), ["upMbps"] = Math.Round(up * 8 / 1e6, 1), ["latencyMs"] = Math.Round(pings[pings.Count / 2], 1), ["jitterMs"] = Math.Round(pings.Zip(pings.Skip(1), (a, b) => Math.Abs(b - a)).DefaultIfEmpty().Average(), 1) },
            Note = Loc.T("Ölçüm Cloudflare'in herkese açık hız testi sunucusuyla yapılır. Puan, indirme hızının Mbps cinsinden değeridir.", "Measured against Cloudflare's public speed test server. The score is the download speed in Mbps."),
        });
    }

    private static double Transfer(HttpClient http, bool download, JobContext job)
    {
        // Parallel streams for 8 seconds; the rate over the last 6 seconds avoids TCP slow start.
        long bytes = 0, bytesAt2s = 0;
        var sw = Stopwatch.StartNew();
        var payload = new byte[4 << 20];
        Random.Shared.NextBytes(payload);
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            var buffer = new byte[81920];
            while (sw.ElapsedMilliseconds < 8000 && !job.Cancelled)
            {
                try
                {
                    if (download)
                    {
                        using var s = await http.GetStreamAsync("https://speed.cloudflare.com/__down?bytes=25000000");
                        int n;
                        while ((n = await s.ReadAsync(buffer)) > 0 && sw.ElapsedMilliseconds < 8000) Interlocked.Add(ref bytes, n);
                    }
                    else
                    {
                        using var content = new ByteArrayContent(payload);
                        using var r = await http.PostAsync("https://speed.cloudflare.com/__up", content);
                        if (r.IsSuccessStatusCode) Interlocked.Add(ref bytes, payload.Length);
                    }
                }
                catch { await Task.Delay(200); }
            }
        })).ToArray();
        while (!Task.WaitAll(tasks, 250))
            if (sw.ElapsedMilliseconds >= 2000 && bytesAt2s == 0) bytesAt2s = Math.Max(1, Interlocked.Read(ref bytes));
        double seconds = Math.Max(1, sw.Elapsed.TotalSeconds - 2);
        return Math.Max(0, bytes - bytesAt2s) / seconds;
    }

    // ------------------------------------------------------------------ stability
    private BenchResult Stability(int minutes, JobContext job)
    {
        var duration = TimeSpan.FromMinutes(minutes);
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Token);
        var threads = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => new Thread(() => { try { CpuWork(duration, cts.Token); } catch (OperationCanceledException) { } }) { Priority = ThreadPriority.BelowNormal }).ToList();
        threads.ForEach(t => t.Start());
        var samples = new List<(double Mhz, double? Temp, bool Throttled, double Cpu)>();
        var timeline = new List<double[]>();
        try
        {
            while (sw.Elapsed < duration)
            {
                Thread.Sleep(1000);
                job.ThrowIfCancelled();
                metrics.Touch();
                if (metrics.Latest is { } s)
                {
                    samples.Add((s.CpuMhz, s.Temp, s.Throttled, s.Cpu));
                    timeline.Add([Math.Round(sw.Elapsed.TotalSeconds), Math.Round(s.CpuMhz), s.Temp ?? 0, s.Throttled ? 1 : 0]);
                    // Safety stop: never cook the machine for a test.
                    if (s.Temp is > 98) { cts.Cancel(); break; }
                }
                job.Report(sw.Elapsed / duration, Loc.T("Yük altında: {0:0} MHz, {1}", "Under load: {0:0} MHz, {1}").F(s_mhz(samples), samples.LastOrDefault().Temp is double tc ? $"{tc:0} °C" : "–"));
            }
        }
        finally { cts.Cancel(); threads.ForEach(t => t.Join()); }

        static double s_mhz(List<(double Mhz, double? Temp, bool Throttled, double Cpu)> s) => s.Count > 0 ? s[^1].Mhz : 0;
        if (samples.Count < 10) throw RpcException.Failed("Too few samples");
        int n = samples.Count;
        double first = samples.Take(Math.Max(1, n / 6)).Average(x => x.Mhz), last = samples.Skip(n - n / 3).Average(x => x.Mhz);
        double drop = first > 0 ? 100 * (first - last) / first : 0;
        double throttled = 100.0 * samples.Count(x => x.Throttled) / n;
        double? maxTemp = samples.Where(x => x.Temp is not null).Select(x => x.Temp).Max();
        bool stopped = maxTemp is > 98;
        LText note = stopped ? Loc.T("Sıcaklık 98 °C'yi geçtiği için test güvenlik amacıyla durduruldu. Soğutma yetersiz.", "The test stopped for safety because the temperature exceeded 98 °C. Cooling is insufficient.")
            : drop > 15 || throttled > 30 ? Loc.T("Uzun yük altında işlemci hızı belirgin biçimde düştü: soğutma sürekli yükü taşıyamıyor. Toz temizliği ya da termal macun yenilemesi önerilir.", "Under sustained load the CPU speed dropped noticeably: cooling cannot sustain the load. Cleaning dust or renewing thermal paste is recommended.")
            : drop > 5 ? Loc.T("Hafif hız düşüşü: dizüstü bilgisayarlar için normaldir (turbo süre sınırı).", "A slight speed drop: normal for laptops (turbo time limit).")
            : Loc.T("Kararlı: işlemci yük altında hızını korudu.", "Stable: the CPU held its speed under load.");
        return Save(new BenchResult
        {
            Kind = "stability", Label = $"{minutes} min",
            Score = Math.Round(Math.Max(0, 100 - drop * 2 - throttled / 2)),
            Metrics = new() { ["startMhz"] = Math.Round(first), ["endMhz"] = Math.Round(last), ["dropPct"] = Math.Round(drop, 1), ["throttledPct"] = Math.Round(throttled, 1), ["maxTemp"] = maxTemp ?? 0, ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds) },
            Info = new() { ["timeline"] = System.Text.Json.JsonSerializer.Serialize(timeline) },
            Note = note,
        });
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("bench.rank", _ => HardwareRank.Evaluate(system.Hardware(), store.ReadLines<BenchResult>(HistoryFile)));
        rpc.On("bench.history", _ => store.ReadLines<BenchResult>(HistoryFile).OrderByDescending(r => r.Time).Take(200).ToList());
        rpc.On("bench.run", c =>
        {
            string kind = c.Str("kind");
            var hw = system.Hardware();
            return kind switch
            {
                "cpu" => jobs.Start("bench", Loc.T("İşlemci testi", "CPU benchmark"), Cpu),
                "memory" => jobs.Start("bench", Loc.T("Bellek testi", "Memory benchmark"), job => MemoryBench(job, Save, hw.Memory.Modules.FirstOrDefault() is { } m ? $"{new ByteSize(hw.Memory.InstalledBytes)} {m.Type}-{m.ConfiguredMhz}" : new ByteSize(hw.Memory.InstalledBytes).ToString())),
                "disk" => jobs.Start("bench", Loc.T("Disk testi", "Disk benchmark"), job =>
                {
                    string drive = c.Str("drive", Paths.SystemDrive);
                    var disk = hw.Disks.FirstOrDefault(d => d.Letters.Contains(drive[..1], StringComparer.OrdinalIgnoreCase));
                    return DiskBench(drive, Math.Clamp(c.Int("sizeMb", 1024), 256, 4096), job, Save, disk?.Model ?? drive);
                }),
                "network" => jobs.Start("bench", Loc.T("İnternet hız testi", "Internet speed test"), NetworkBench),
                "stability" => jobs.Start("bench", Loc.T("Kararlılık testi", "Stability test"), job => Stability(Math.Clamp(c.Int("minutes", 5), 1, 30), job)),
                _ => throw RpcException.Invalid("Unknown benchmark"),
            };
        });
        // The GPU test runs in the UI (WebGL); the UI stores its result here.
        rpc.On("bench.saveGpu", c => Save(new BenchResult
        {
            Kind = "gpu", Label = c.Str("label", system.Hardware().Gpus.FirstOrDefault()?.Name ?? "GPU"), Score = Math.Round(c.Dbl("score")),
            Metrics = new() { ["fps"] = Math.Round(c.Dbl("fps"), 1), ["gflops"] = Math.Round(c.Dbl("gflops"), 1) },
            Note = Loc.T("WebGL üzerinden ölçülür; oyunlardaki DirectX/Vulkan performansıyla birebir aynı değildir ama aynı bilgisayarda önce/sonra karşılaştırması için güvenilirdir.", "Measured through WebGL; not identical to DirectX/Vulkan game performance, but reliable for before/after comparison on the same PC."),
        }));
        rpc.On("bench.delete", c =>
        {
            string id = c.Str("id");
            var all = store.ReadLines<BenchResult>(HistoryFile).Where(r => r.Id != id).ToList();
            File.WriteAllLines(store.PathOf(HistoryFile), all.Select(r => System.Text.Json.JsonSerializer.Serialize(r, Json.StoreLine)));
            return true;
        });
    }
}
