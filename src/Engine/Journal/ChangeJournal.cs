using System.Security;
using Microsoft.Win32;
using OptiCore.Engine.Core;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Journal;

/// <summary>One atomic modification of the system, with the state before and after.</summary>
public sealed class ChangeEntry
{
    /// <summary>reg.value | reg.key | service.start | task.enabled | power.scheme | command | note | custom kinds.</summary>
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
    public Dictionary<string, string?> Before { get; set; } = [];
    public Dictionary<string, string?> After { get; set; } = [];
    public bool Reversible { get; set; } = true;
    public bool Applied { get; set; }
    public bool Reverted { get; set; }
    public string? Error { get; set; }
}

/// <summary>A group of changes made by one user action ("applied the Gaming preset", "cleanup run").</summary>
public sealed class ChangeSet
{
    public string Id { get; set; } = "";
    public DateTime Time { get; set; }
    public string Source { get; set; } = "";
    public LText Title { get; set; }
    public List<ChangeEntry> Entries { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    /// <summary>True when OptiCore ran in simulation mode: nothing was actually modified.</summary>
    public bool Simulated { get; set; }
    public DateTime? RevertedAt { get; set; }
    public long BytesFreed { get; set; }
    /// <summary>none | explorer | logoff | reboot</summary>
    public string Restart { get; set; } = "none";

    public int Applied => Entries.Count(e => e.Applied);
    public int Failed => Entries.Count(e => e.Error is not null);
    public bool CanRevert => !Simulated && RevertedAt is null && Entries.Any(e => e.Applied && e.Reversible && !e.Reverted);
}

/// <summary>
/// The safety net behind every optimization: nothing changes the system without being recorded here
/// first, and everything recorded as reversible can be undone later, exactly.
/// </summary>
public sealed class ChangeJournal
{
    private readonly DataStore _store;
    private readonly object _gate = new();
    private readonly List<ChangeSet> _sets = [];
    private readonly Dictionary<string, Action<ChangeEntry>> _undo = new(StringComparer.Ordinal);

    public bool DryRun { get; }
    public event Action<ChangeSet>? Recorded;

    public ChangeJournal(DataStore store, bool dryRun)
    {
        _store = store;
        DryRun = dryRun;
        try
        {
            foreach (var file in Directory.EnumerateFiles(store.DirOf("journal"), "*.json").OrderBy(f => f))
            {
                var set = store.Load<ChangeSet?>(Path.Combine("journal", Path.GetFileName(file)), () => null);
                if (set is not null) _sets.Add(set);
            }
        }
        catch (Exception e) { Log.Warn("journal: load failed", e); }

        _undo["reg.value"] = e => RegState.Write(e.After["path"]!, e.After.GetValueOrDefault("name") ?? "", e.Before, View(e));
        _undo["reg.key"] = e =>
        {
            var node = _store.Load<RegNode?>(Path.Combine("journal", "blobs", e.Before["blob"]!), () => null) ?? throw new InvalidOperationException("Backup of the deleted key is missing");
            node.Restore(e.Target, View(e));
        };
        _undo["service.start"] = e => ServiceOps.SetStartMode(e.Target, e.Before["mode"]!);
        _undo["task.enabled"] = e => TaskSched.SetEnabled(e.Target, e.Before["enabled"] == "1");
        _undo["power.scheme"] = e => { if (!PowerPlans.SetActive(Guid.Parse(e.Before["guid"]!))) throw new InvalidOperationException("Could not restore the power plan"); };
        _undo["command"] = e =>
        {
            var r = Proc.Run(e.Before["file"]!, e.Before["args"] ?? "", 120000);
            if (!r.Ok) throw new InvalidOperationException(r.Text);
        };
    }

    private static RegistryView View(ChangeEntry e) => e.After.GetValueOrDefault("view") == "32" ? RegistryView.Registry32 : RegistryView.Registry64;

    /// <summary>Lets a module teach the journal how to undo its own change kind.</summary>
    public void RegisterUndo(string kind, Action<ChangeEntry> undo) => _undo[kind] = undo;

    public ChangeScope Begin(string source, LText title, params string[] tags) => new(this, new ChangeSet
    {
        Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}",
        Time = DateTime.UtcNow, Source = source, Title = title, Tags = [.. tags], Simulated = DryRun,
    });

    internal void Save(ChangeSet set)
    {
        lock (_gate)
        {
            if (!_sets.Contains(set)) _sets.Add(set);
            _store.Save(Path.Combine("journal", set.Id + ".json"), set);
        }
    }

    internal void Commit(ChangeSet set)
    {
        Save(set);
        try { Recorded?.Invoke(set); } catch { /* listeners must not break the caller */ }
    }

    internal string SaveBlob(ChangeSet set, RegNode node)
    {
        var name = $"{set.Id}-{set.Entries.Count}.json";
        _store.Save(Path.Combine("journal", "blobs", name), node);
        return name;
    }

    public List<ChangeSet> List() { lock (_gate) return [.. _sets.OrderByDescending(s => s.Time)]; }
    public ChangeSet? Get(string id) { lock (_gate) return _sets.FirstOrDefault(s => s.Id == id); }

    /// <summary>Undoes a change set (newest entry first). Returns how many entries were restored and failed.</summary>
    public (int Restored, int Failed) Revert(string id, int[]? onlyEntries = null)
    {
        var set = Get(id) ?? throw RpcException.NotFound("Change set not found");
        if (set.Simulated) return (0, 0);
        int ok = 0, failed = 0;
        for (int i = set.Entries.Count - 1; i >= 0; i--)
        {
            var e = set.Entries[i];
            if (!e.Applied || !e.Reversible || e.Reverted) continue;
            if (onlyEntries is not null && !onlyEntries.Contains(i)) continue;
            if (!_undo.TryGetValue(e.Kind, out var undo)) { failed++; continue; }
            try { undo(e); e.Reverted = true; e.Error = null; ok++; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException) { e.Error = "ELEVATION_REQUIRED"; failed++; }
            catch (Exception ex) { e.Error = ex.Message; failed++; }
        }
        if (set.Entries.All(e => !e.Applied || !e.Reversible || e.Reverted)) set.RevertedAt = DateTime.UtcNow;
        Save(set);
        if (failed > 0 && ok == 0 && set.Entries.Any(e => e.Error == "ELEVATION_REQUIRED") && !Elevation.IsElevated) throw RpcException.Elevation();
        return (ok, failed);
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("journal.list", _ => List().Select(s => new
        {
            s.Id, s.Time, s.Source, s.Title, s.Tags, s.Simulated, s.RevertedAt, s.BytesFreed, s.Restart,
            s.Applied, s.Failed, s.CanRevert, count = s.Entries.Count,
        }).ToList());
        rpc.On("journal.get", c => Get(c.Str("id")) ?? throw RpcException.NotFound("Change set not found"));
        rpc.On("journal.revert", c =>
        {
            var (restored, failed) = Revert(c.Str("id"), c.Has("entries") ? c.IntArr("entries") : null);
            return new { restored, failed };
        });
    }
}

/// <summary>Collects the changes of one action. Every method records first, then applies (unless simulating).</summary>
public sealed class ChangeScope : IDisposable
{
    private readonly ChangeJournal _journal;
    private bool _committed;
    public ChangeSet Set { get; }
    public bool DryRun => _journal.DryRun;

    internal ChangeScope(ChangeJournal journal, ChangeSet set) { _journal = journal; Set = set; }

    private ChangeEntry Add(string kind, string target, Dictionary<string, string?> before, Dictionary<string, string?> after, bool reversible, Action apply)
    {
        var entry = new ChangeEntry { Kind = kind, Target = target, Before = before, After = after, Reversible = reversible };
        Set.Entries.Add(entry);
        if (DryRun) return entry;
        try { apply(); entry.Applied = true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException) { entry.Error = "ELEVATION_REQUIRED"; }
        catch (Exception ex) { entry.Error = ex.Message; Log.Warn($"journal: {kind} {target}", ex); }
        return entry;
    }

    public ChangeEntry RegSet(string path, string name, RegistryValueKind kind, object value, RegistryView view = RegistryView.Registry64)
    {
        var after = new Dictionary<string, string?> { ["exists"] = "1", ["kind"] = kind.ToString(), ["data"] = RegState.Encode(value), ["name"] = name, ["path"] = path };
        if (view == RegistryView.Registry32) after["view"] = "32";
        return Add("reg.value", path + "::" + name, RegState.Read(path, name, view), after, true, () =>
        {
            using var key = Reg.Create(path, view);
            key.SetValue(name, value, kind);
        });
    }

    public ChangeEntry RegDeleteValue(string path, string name, RegistryView view = RegistryView.Registry64)
    {
        var after = new Dictionary<string, string?> { ["exists"] = "0", ["name"] = name, ["path"] = path };
        if (view == RegistryView.Registry32) after["view"] = "32";
        return Add("reg.value", path + "::" + name, RegState.Read(path, name, view), after, true, () =>
        {
            using var key = Reg.Open(path, true, view);
            key?.DeleteValue(name, throwOnMissingValue: false);
        });
    }

    /// <summary>Applies a registry state captured earlier (used to restore defaults).</summary>
    public ChangeEntry RegApply(string path, string name, RegistryValueKind kind, object? value, RegistryView view = RegistryView.Registry64) =>
        value is null ? RegDeleteValue(path, name, view) : RegSet(path, name, kind, value, view);

    public ChangeEntry RegDeleteKey(string path, RegistryView view = RegistryView.Registry64)
    {
        var before = new Dictionary<string, string?>();
        var node = RegNode.Capture(path, view);
        if (node is not null && !DryRun) before["blob"] = _journal.SaveBlob(Set, node);
        var after = new Dictionary<string, string?> { ["exists"] = "0" };
        if (view == RegistryView.Registry32) after["view"] = "32";
        return Add("reg.key", path, before, after, node is not null, () =>
        {
            var (hive, sub) = Reg.Parse(path);
            using var root = RegistryKey.OpenBaseKey(hive, view);
            root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
        });
    }

    public ChangeEntry Service(string name, string startMode) =>
        Add("service.start", name,
            new() { ["mode"] = ServiceOps.StartMode(name) }, new() { ["mode"] = startMode },
            reversible: ServiceOps.StartMode(name) is "auto" or "delayed-auto" or "demand" or "disabled",
            () => ServiceOps.SetStartMode(name, startMode));

    public ChangeEntry Task(string taskPath, bool enabled)
    {
        bool? was = TaskSched.GetEnabled(taskPath);
        return Add("task.enabled", taskPath,
            new() { ["enabled"] = was == true ? "1" : "0" }, new() { ["enabled"] = enabled ? "1" : "0" },
            reversible: was is not null, () => TaskSched.SetEnabled(taskPath, enabled));
    }

    public ChangeEntry PowerScheme(Guid scheme)
    {
        var was = PowerPlans.Active();
        return Add("power.scheme", "Power plan",
            new() { ["guid"] = was?.ToString() }, new() { ["guid"] = scheme.ToString() },
            reversible: was is not null, () =>
            {
                if (!PowerPlans.SetActive(scheme)) throw new InvalidOperationException("PowerSetActiveScheme failed");
            });
    }

    /// <summary>Runs a system tool. Supply the inverse command to make the change reversible.</summary>
    public ChangeEntry Command(string target, string file, string args, string? undoFile = null, string? undoArgs = null, int timeoutMs = 120000) =>
        Add("command", target,
            new() { ["file"] = undoFile, ["args"] = undoArgs }, new() { ["file"] = file, ["args"] = args },
            reversible: undoFile is not null, () =>
            {
                var r = Proc.Run(file, args, timeoutMs);
                if (!r.Ok) throw r.ExitCode == 5 ? new UnauthorizedAccessException(r.Text) : new InvalidOperationException(r.Text.Length > 0 ? r.Text : $"exit code {r.ExitCode}");
            });

    /// <summary>A change with module-specific undo logic (see <see cref="ChangeJournal.RegisterUndo"/>).</summary>
    public ChangeEntry Custom(string kind, string target, Dictionary<string, string?> before, Dictionary<string, string?> after, Action apply, bool reversible = true) =>
        Add(kind, target, before, after, reversible, apply);

    /// <summary>Records something that cannot be undone (deleted cache files, a killed process).</summary>
    public ChangeEntry Note(string kind, string target, Dictionary<string, string?>? info = null, bool applied = true)
    {
        var entry = new ChangeEntry { Kind = kind, Target = target, After = info ?? [], Reversible = false, Applied = applied && !DryRun };
        Set.Entries.Add(entry);
        return entry;
    }

    public bool NeedsElevation => Set.Entries.Any(e => e.Error == "ELEVATION_REQUIRED");

    public ChangeSet Commit()
    {
        if (!_committed && Set.Entries.Count > 0) { _committed = true; _journal.Commit(Set); }
        return Set;
    }

    public void Dispose() => Commit();
}
