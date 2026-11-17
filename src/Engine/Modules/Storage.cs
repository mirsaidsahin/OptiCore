using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public sealed class StoragePoint { public long T { get; set; } public string Letter { get; set; } = ""; public long Free { get; set; } public long Total { get; set; } }
public sealed class FolderPoint { public long T { get; set; } public string Path { get; set; } = ""; public long Size { get; set; } public long Files { get; set; } }

/// <summary>Storage Intelligence: what occupies the disk, how it changes over time, and when it will be full.</summary>
public sealed class StorageService
{
    private sealed class Node
    {
        public string Name = "";
        public string Path = "";
        public long Size;
        public long Files;
        public long OwnBytes;        // files directly in this folder
        public List<Node> Children = [];
    }

    private static readonly Dictionary<string, string> Types = BuildTypes();
    private readonly SystemInfoService _system;
    private readonly DataStore _store;
    private readonly JobManager _jobs;
    private readonly ChangeJournal _journal;
    private Node? _tree;
    private Timer? _timer;

    public StorageService(SystemInfoService system, DataStore store, JobManager jobs, ChangeJournal journal)
    {
        _system = system; _store = store; _jobs = jobs; _journal = journal;
    }

    public void Start() => _timer = new Timer(_ => { try { Record(); } catch (Exception e) { Log.Warn("storage: record", e); } }, null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(3));

    private static Dictionary<string, string> BuildTypes()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string type, params string[] exts) { foreach (var e in exts) map["." + e] = type; }
        Add("video", "mp4", "mkv", "avi", "mov", "wmv", "webm", "m4v", "flv", "ts", "mpg", "mpeg");
        Add("image", "jpg", "jpeg", "png", "gif", "bmp", "webp", "heic", "tif", "tiff", "raw", "cr2", "nef", "arw", "psd", "svg", "ico");
        Add("audio", "mp3", "flac", "wav", "aac", "ogg", "m4a", "wma", "opus");
        Add("document", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "odt", "ods", "rtf", "epub", "csv", "md");
        Add("archive", "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "img", "cab", "msi", "vhd", "vhdx", "wim");
        Add("program", "exe", "dll", "sys", "msix", "appx", "ocx", "drv", "bin", "pak", "node");
        Add("game", "vpk", "bsa", "ba2", "uasset", "umap", "upk", "scs", "gar", "dds", "assets", "bundle", "wad", "rpf", "forge", "utoc", "ucas");
        Add("code", "js", "ts", "py", "cs", "cpp", "c", "h", "java", "go", "rs", "json", "xml", "html", "css", "php", "rb", "sql", "map", "pdb", "obj", "lib", "pyc");
        Add("data", "db", "sqlite", "mdf", "ldf", "dat", "log", "etl", "dmp", "tmp", "cache", "bak", "edb");
        return map;
    }

    // ------------------------------------------------------------------ tree scan
    private object Scan(string root, JobContext job)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw RpcException.NotFound(Loc.S("Klasör bulunamadı.", "Folder not found."));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var largest = new PriorityQueue<FileEntry, long>();
        var byType = new Dictionary<string, long>();
        long files = 0, folders = 0, denied = 0, cloud = 0, total = 0;

        // Progress against the used space of the volume, when a whole drive is scanned.
        long expected = 0;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            if (root.TrimEnd('\\').Length <= 2) expected = drive.TotalSize - drive.TotalFreeSpace;
        }
        catch { /* network path */ }

        Node Walk(string path, string name, int depth)
        {
            var node = new Node { Name = name, Path = path };
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(path).EnumerateFileSystemInfos("*", FileCleaner.Flat); }
            catch { denied++; return node; }
            try
            {
                foreach (var entry in entries)
                {
                    if (entry is FileInfo f)
                    {
                        long size;
                        try
                        {
                            // Cloud placeholders (OneDrive "online-only") report their full size but occupy nothing.
                            if ((f.Attributes & (FileAttributes.Offline | (FileAttributes)0x400000 | (FileAttributes)0x40000)) != 0) { cloud += f.Length; continue; }
                            size = f.Length;
                        }
                        catch { continue; }
                        node.OwnBytes += size;
                        node.Files++;
                        files++;
                        total += size;
                        string type = Types.GetValueOrDefault(f.Extension, "other");
                        byType[type] = byType.GetValueOrDefault(type) + size;
                        if (size > 20 << 20)
                        {
                            largest.Enqueue(new FileEntry(f.FullName, size, f.LastWriteTimeUtc), size);
                            if (largest.Count > 300) largest.Dequeue();
                        }
                        if ((files & 0xFFF) == 0)
                        {
                            job.ThrowIfCancelled();
                            job.Report(expected > 0 ? Math.Min(0.99, total / (double)expected) : -1,
                                Loc.T("{0:N0} dosya, {1} tarandı", "{0:N0} files, {1} scanned").F(files, new ByteSize(total)));
                        }
                    }
                    else if (entry is DirectoryInfo d)
                    {
                        folders++;
                        var child = Walk(d.FullName, d.Name, depth + 1);
                        node.Size += child.Size;
                        node.Files += child.Files;
                        // Deep, tiny folders are folded into their parent to keep the tree small.
                        if (depth < 9 && (child.Size > 1 << 20 || depth < 3)) node.Children.Add(child);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { denied++; }
            node.Size += node.OwnBytes;
            return node;
        }

        _tree = Walk(root, root, 0);
        string[] order = ["video", "image", "audio", "document", "archive", "program", "game", "code", "data", "other"];
        return new
        {
            root, size = _tree.Size, files, folders, denied, cloudBytes = cloud, seconds = sw.Elapsed.TotalSeconds,
            types = order.Where(byType.ContainsKey).Select(t => new { type = t, bytes = byType[t] }).ToList(),
            largest = largest.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Bytes).ToList(),
            node = View(_tree),
        };
    }

    private static object View(Node n)
    {
        var children = n.Children.OrderByDescending(c => c.Size).Take(80).Select(c => new { name = c.Name, path = c.Path, size = c.Size, files = c.Files, folder = true, hasChildren = c.Children.Count > 0 }).ToList();
        long rest = n.Size - n.OwnBytes - children.Sum(c => c.size);
        return new
        {
            name = n.Name, path = n.Path, size = n.Size, files = n.Files,
            children,
            ownBytes = n.OwnBytes,   // files that sit directly in this folder
            otherBytes = Math.Max(0, rest),
        };
    }

    private object NodeAt(string path)
    {
        if (_tree is null) throw RpcException.NotFound(Loc.S("Önce bir tarama yapın.", "Run a scan first."));
        var node = _tree;
        if (!path.Equals(node.Path, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in path[node.Path.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                node = node.Children.FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase)) ?? throw RpcException.NotFound(Loc.S("Bu klasörün ayrıntısı taramada tutulmadı.", "No detail was kept for this folder."));
        }
        return View(node);
    }

    // ------------------------------------------------------------------ trends
    private void Record()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const string file = "history/storage.jsonl";
        var existing = _store.ReadLines<StoragePoint>(file);
        long lastAt = existing.Count > 0 ? existing[^1].T : 0;
        if (now - lastAt < TimeSpan.FromHours(6).TotalMilliseconds) return;
        _system.Hardware();
        foreach (var v in _system.Volumes().Where(v => v.Kind == "fixed"))
            _store.Append(file, new StoragePoint { T = now, Letter = v.Letter, Free = v.FreeBytes, Total = v.TotalBytes });
        if (existing.Count > 6000) _store.TrimLines(file, 4000);

        foreach (var path in _store.Load(Path.Combine("storage", "watched.json"), () => new List<string>()))
        {
            try
            {
                if (!Directory.Exists(path)) continue;
                long size = 0, files = 0;
                foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", FileCleaner.Walk)) { size += f.Length; files++; }
                _store.Append("history/folders.jsonl", new FolderPoint { T = now, Path = path, Size = size, Files = files });
            }
            catch { /* folder not readable right now */ }
        }
    }

    /// <summary>Least-squares slope of used space, in bytes per day.</summary>
    private static double? Slope(List<StoragePoint> points)
    {
        if (points.Count < 3) return null;
        double t0 = points[0].T;
        var xs = points.Select(p => (p.T - t0) / 86400000.0).ToList();
        if (xs[^1] < 2) return null; // less than two days of history says nothing
        var ys = points.Select(p => (double)(p.Total - p.Free)).ToList();
        double mx = xs.Average(), my = ys.Average();
        double den = xs.Sum(x => (x - mx) * (x - mx));
        return den <= 0 ? null : xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum() / den;
    }

    private sealed record TrendPoint(long T, long Used);
    private sealed record Trend(string Letter, long Total, long Free, List<TrendPoint> Points, double? BytesPerDay, double? DaysUntilFull);

    private List<Trend> Trends()
    {
        var all = _store.ReadLines<StoragePoint>("history/storage.jsonl");
        long since = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds();
        long month = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
        return [.. all.Where(p => p.T >= since).GroupBy(p => p.Letter).Select(g =>
        {
            var points = g.OrderBy(p => p.T).ToList();
            double? slope = Slope([.. points.Where(p => p.T >= month)]);
            var last = points[^1];
            // Only a clear upward trend (over 1 MB a day) yields a forecast.
            double? daysLeft = slope is double s && s > 1 << 20 ? last.Free / s : null;
            return new Trend(g.Key, last.Total, last.Free, [.. points.Select(p => new TrendPoint(p.T, p.Total - p.Free))], slope, daysLeft);
        })];
    }

    public IEnumerable<Finding> Findings()
    {
        foreach (var t in Trends())
        {
            if (t.DaysUntilFull is not double days || days > 60) continue;
            yield return new Finding
            {
                Id = "storage.forecast." + t.Letter, Group = "storage", Severity = days < 14 ? Severity.Critical : Severity.Warning, Penalty = days < 14 ? 20 : 8,
                Title = Loc.T("{0}: sürücüsü bu hızla yaklaşık {1:0} gün içinde dolacak", "Drive {0}: will be full in about {1:0} days at this rate").F(t.Letter, days),
                Detail = Loc.T("Son 30 günde kullanılan alan düzenli olarak artıyor. Tahmin, bu artışın aynı hızla süreceğini varsayar.", "Used space has grown steadily over the last 30 days. The forecast assumes it keeps growing at the same rate."),
            }.Ev(Loc.T("Günlük artış", "Growth per day"), new ByteSize((long)(t.BytesPerDay ?? 0)).ToString()).Ev(Loc.T("Boş alan", "Free"), new ByteSize(t.Free).ToString())
             .Go(Loc.T("Yer kaplayanları bul", "Find what takes space"), "storage/map");
        }
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("storage.scan", c =>
        {
            string root = c.Str("root", Paths.SystemDrive);
            return _jobs.Start("storage.scan", Loc.T("Disk kullanım taraması", "Disk usage scan"), job => Scan(root, job));
        });
        rpc.On("storage.node", c => NodeAt(c.Str("path")));
        rpc.On("storage.trends", _ => Trends());
        rpc.Do("storage.record", _ => Record());
        rpc.On("storage.watched", _ =>
        {
            var watched = _store.Load(Path.Combine("storage", "watched.json"), () => new List<string>());
            var history = _store.ReadLines<FolderPoint>("history/folders.jsonl");
            return watched.Select(p => new { path = p, points = history.Where(h => h.Path.Equals(p, StringComparison.OrdinalIgnoreCase)).Select(h => new { t = h.T, size = h.Size, files = h.Files }).ToList() }).ToList();
        });
        rpc.On("storage.watch", c =>
        {
            string path = c.Str("path");
            bool remove = c.Bool("remove");
            if (!remove && !Directory.Exists(path)) throw RpcException.NotFound(Loc.S("Klasör bulunamadı.", "Folder not found."));
            var watched = _store.Load(Path.Combine("storage", "watched.json"), () => new List<string>());
            watched.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (!remove) watched.Add(Path.GetFullPath(path));
            _store.Save(Path.Combine("storage", "watched.json"), watched);
            return watched;
        });
    }
}
