using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptiCore.Engine.Core;

/// <summary>Minimal file logger. Everything OptiCore does on the machine is traceable here.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _dir;

    public static void Init(string dir)
    {
        _dir = dir;
        try { Directory.CreateDirectory(dir); } catch { /* logging must never break the app */ }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? e = null) => Write("WARN", message, e);
    public static void Error(string message, Exception? e = null) => Write("ERROR", message, e);

    private static void Write(string level, string message, Exception? e)
    {
        if (_dir is null) return;
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}" + (e is null ? "" : $" :: {e.GetType().Name}: {e.Message}");
            lock (Gate) File.AppendAllText(Path.Combine(_dir, $"opticore-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* ignore */ }
    }
}

/// <summary>
/// All persistent state lives as plain JSON under one folder (%LOCALAPPDATA%\OptiCore by default).
/// No database, no registry: the user can read, back up or delete everything OptiCore remembers.
/// </summary>
public sealed class DataStore
{
    private readonly object _gate = new();
    public string Root { get; }

    public DataStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string PathOf(params string[] parts)
    {
        var p = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        return p;
    }

    public string DirOf(params string[] parts)
    {
        var p = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    public T Load<T>(string rel, Func<T> fallback)
    {
        try
        {
            var p = PathOf(rel);
            if (!File.Exists(p)) return fallback();
            lock (_gate) return JsonSerializer.Deserialize<T>(File.ReadAllText(p, Encoding.UTF8), Json.Store) ?? fallback();
        }
        catch (Exception e)
        {
            Log.Warn($"store: could not read {rel}", e);
            return fallback();
        }
    }

    public void Save<T>(string rel, T value)
    {
        try
        {
            var p = PathOf(rel);
            var tmp = p + ".tmp";
            lock (_gate)
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json.Store), Encoding.UTF8);
                File.Move(tmp, p, overwrite: true);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"store: could not write {rel}", e);
        }
    }

    public void Append(string rel, object record)
    {
        try
        {
            var p = PathOf(rel);
            lock (_gate) File.AppendAllText(p, JsonSerializer.Serialize(record, Json.StoreLine) + "\n", Encoding.UTF8);
        }
        catch (Exception e)
        {
            Log.Warn($"store: could not append {rel}", e);
        }
    }

    public List<T> ReadLines<T>(string rel)
    {
        var list = new List<T>();
        try
        {
            var p = PathOf(rel);
            if (!File.Exists(p)) return list;
            string[] lines;
            lock (_gate) lines = File.ReadAllLines(p, Encoding.UTF8);
            foreach (var line in lines)
            {
                if (line.Length < 2) continue;
                try { var v = JsonSerializer.Deserialize<T>(line, Json.StoreLine); if (v is not null) list.Add(v); }
                catch { /* skip a damaged line */ }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"store: could not read {rel}", e);
        }
        return list;
    }

    /// <summary>Rewrites a JSON-lines file keeping only the newest <paramref name="keep"/> records.</summary>
    public void TrimLines(string rel, int keep)
    {
        try
        {
            var p = PathOf(rel);
            if (!File.Exists(p)) return;
            lock (_gate)
            {
                var lines = File.ReadAllLines(p, Encoding.UTF8);
                if (lines.Length > keep) File.WriteAllLines(p, lines[^keep..], Encoding.UTF8);
            }
        }
        catch { /* ignore */ }
    }
}

public sealed class AlertSettings
{
    public bool Enabled { get; set; } = true;
    public int CpuPercent { get; set; } = 90;
    public int CpuSeconds { get; set; } = 90;
    public int RamPercent { get; set; } = 92;
    public int DiskFreePercent { get; set; } = 10;
    public int TemperatureC { get; set; } = 92;
    public bool StartupGuard { get; set; } = true;
    public bool LeakDetection { get; set; } = true;
}

public sealed class AppSettings
{
    public string Language { get; set; } = "tr";
    public string Theme { get; set; } = "dark";
    public string Accent { get; set; } = "violet";
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool ReduceMotion { get; set; }
    public AlertSettings Alerts { get; set; } = new();
    /// <summary>Finding ids the user chose to hide.</summary>
    public List<string> IgnoredFindings { get; set; } = [];
    /// <summary>Free-form state owned by the UI (dashboard layout, dismissed hints, table sort orders).</summary>
    public Dictionary<string, JsonElement> Ui { get; set; } = [];
}

public sealed class SettingsService(DataStore store)
{
    private const string FileName = "settings.json";
    private readonly object _gate = new();
    public AppSettings Current { get; private set; } = store.Load(FileName, () => new AppSettings());
    public event Action<AppSettings>? Changed;

    /// <summary>Deep-merges a partial JSON object into the settings and saves them.</summary>
    public AppSettings Patch(JsonElement patch)
    {
        lock (_gate)
        {
            var node = JsonSerializer.SerializeToNode(Current, Json.Store)!.AsObject();
            if (patch.ValueKind == JsonValueKind.Object) Merge(node, JsonNode.Parse(patch.GetRawText())!.AsObject());
            Current = node.Deserialize<AppSettings>(Json.Store) ?? Current;
            store.Save(FileName, Current);
        }
        Loc.Default = Current.Language == "en" ? "en" : "tr";
        Changed?.Invoke(Current);
        return Current;
    }

    public void Update(Action<AppSettings> change)
    {
        lock (_gate)
        {
            change(Current);
            store.Save(FileName, Current);
        }
        Changed?.Invoke(Current);
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (value is JsonObject po && target[key] is JsonObject to) Merge(to, po);
            else target[key] = value?.DeepClone();
        }
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("settings.get", _ => Current);
        rpc.On("settings.patch", c => Patch(c.P));
    }
}

public enum NoticeLevel { Info, Success, Warn, Critical }

public sealed class Notice
{
    public string Id { get; set; } = "";
    public DateTime Time { get; set; }
    public NoticeLevel Level { get; set; }
    public LText Title { get; set; }
    public LText Body { get; set; }
    /// <summary>UI route to open when the notice is clicked.</summary>
    public string? Route { get; set; }
    /// <summary>Dedupe key: a notice with the same key is not repeated within the cool-down.</summary>
    public string? Key { get; set; }
    public bool Read { get; set; }
}

/// <summary>In-app notification center; the desktop shell also forwards new notices to the tray.</summary>
public sealed class NotificationCenter
{
    private const string FileName = "notifications.json";
    private readonly DataStore _store;
    private readonly List<Notice> _items;
    private readonly object _gate = new();
    public event Action<Notice>? Added;

    public NotificationCenter(DataStore store)
    {
        _store = store;
        _items = store.Load(FileName, () => new List<Notice>());
    }

    public Notice? Add(NoticeLevel level, LText title, LText body, string? route = null, string? key = null, double coolDownHours = 12)
    {
        Notice n;
        lock (_gate)
        {
            if (key is not null && _items.Any(x => x.Key == key && (DateTime.UtcNow - x.Time).TotalHours < coolDownHours)) return null;
            n = new Notice { Id = Guid.NewGuid().ToString("N")[..12], Time = DateTime.UtcNow, Level = level, Title = title, Body = body, Route = route, Key = key };
            _items.Insert(0, n);
            if (_items.Count > 200) _items.RemoveRange(200, _items.Count - 200);
            _store.Save(FileName, _items);
        }
        try { Added?.Invoke(n); } catch { /* a faulty listener must not break the producer */ }
        return n;
    }

    public List<Notice> List() { lock (_gate) return [.. _items]; }
    public int UnreadCount { get { lock (_gate) return _items.Count(x => !x.Read); } }

    public void MarkRead(string? id)
    {
        lock (_gate)
        {
            foreach (var n in _items.Where(x => id is null || x.Id == id)) n.Read = true;
            _store.Save(FileName, _items);
        }
    }

    public void Clear()
    {
        lock (_gate) { _items.Clear(); _store.Save(FileName, _items); }
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("notices.list", _ => new { items = List(), unread = UnreadCount });
        rpc.Do("notices.read", c => MarkRead(c.StrOrNull("id")));
        rpc.Do("notices.clear", _ => Clear());
    }
}
