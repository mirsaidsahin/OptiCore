using System.Diagnostics;
using System.Management;
using Microsoft.Win32;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

/// <summary>
/// Backup and Recovery: verifies that recorded changes are still in effect, undoes everything OptiCore
/// ever changed, manages Windows restore points and exports the configuration.
/// </summary>
public sealed class BackupService(ChangeJournal journal, TweakService tweaks, SettingsService settings, StartupService startup)
{
    // ------------------------------------------------------------------ verification
    /// <summary>
    /// Compares each recorded change with the live system: still in effect, already back at the old
    /// value, or changed to something else by another program or a Windows update.
    /// </summary>
    private object Verify(string id)
    {
        var set = journal.Get(id) ?? throw RpcException.NotFound("Change set not found");
        var entries = set.Entries.Select((e, index) =>
        {
            string status = "n/a";
            string? live = null;
            if (e.Applied && !e.Reverted)
            {
                try
                {
                    switch (e.Kind)
                    {
                        case "reg.value":
                            var view = e.After.GetValueOrDefault("view") == "32" ? RegistryView.Registry32 : RegistryView.Registry64;
                            var now = RegState.Read(e.After["path"]!, e.After.GetValueOrDefault("name") ?? "", view);
                            live = now["exists"] == "1" ? now.GetValueOrDefault("data") : null;
                            status = Same(now, e.After) ? "intact" : Same(now, e.Before) ? "restored" : "changed";
                            break;
                        case "reg.key":
                            status = Reg.Exists(e.Target) ? "restored" : "intact";
                            break;
                        case "service.start":
                            live = ServiceOps.StartMode(e.Target);
                            status = live == e.After.GetValueOrDefault("mode") ? "intact" : live == e.Before.GetValueOrDefault("mode") ? "restored" : "changed";
                            break;
                        case "task.enabled":
                            live = TaskSched.GetEnabled(e.Target) switch { true => "1", false => "0", _ => null };
                            status = live == e.After.GetValueOrDefault("enabled") ? "intact" : live is null ? "n/a" : "restored";
                            break;
                        case "power.scheme":
                            live = PowerPlans.Active()?.ToString();
                            status = string.Equals(live, e.After.GetValueOrDefault("guid"), StringComparison.OrdinalIgnoreCase) ? "intact" : "changed";
                            break;
                    }
                }
                catch { status = "n/a"; }
            }
            else if (e.Reverted) status = "undone";
            else if (e.Error is not null) status = "failed";
            else if (set.Simulated) status = "simulated";
            return new { index, e.Kind, e.Target, e.Before, e.After, e.Reversible, e.Applied, e.Reverted, e.Error, status, live };
        }).ToList();
        return new
        {
            set.Id, set.Time, set.Source, set.Title, set.Tags, set.Simulated, set.RevertedAt, set.BytesFreed, set.Restart, set.CanRevert, entries,
            drifted = entries.Count(x => x.status is "changed" or "restored"),
        };
    }

    private static bool Same(Dictionary<string, string?> a, Dictionary<string, string?> b)
    {
        if (a.GetValueOrDefault("exists") != b.GetValueOrDefault("exists")) return false;
        return a.GetValueOrDefault("exists") != "1" || string.Equals(a.GetValueOrDefault("data"), b.GetValueOrDefault("data"), StringComparison.OrdinalIgnoreCase);
    }

    private object RevertAll()
    {
        int restored = 0, failed = 0, sets = 0;
        foreach (var set in journal.List().Where(s => s.CanRevert))
        {
            var (ok, bad) = journal.Revert(set.Id);
            restored += ok; failed += bad;
            if (ok > 0) sets++;
        }
        return new { sets, restored, failed };
    }

    // ------------------------------------------------------------------ restore points
    private static object RestorePoints()
    {
        var rows = Wmi.Query("SELECT SequenceNumber, Description, CreationTime, RestorePointType FROM SystemRestore", @"root\default", out var error);
        return new
        {
            denied = error == "denied" || (error is not null && !Elevation.IsElevated),
            unavailable = error is not null && Elevation.IsElevated,
            points = rows.Select(r => new
            {
                id = r.I("SequenceNumber"), description = r.S("Description"), time = r.Date("CreationTime"),
                type = r.I("RestorePointType") switch { 0 => "install", 1 => "uninstall", 10 => "driver", 12 => "settings", 13 => "cancelled", 16 => "manual", 18 => "update", _ => "other" },
            }).OrderByDescending(p => p.id).ToList(),
        };
    }

    private object CreateRestorePoint(string description)
    {
        Elevation.Require();
        if (journal.DryRun) return new { created = false, simulated = true, skipped = false };
        int before = Wmi.Query("SELECT SequenceNumber FROM SystemRestore", @"root\default").Select(r => r.I("SequenceNumber")).DefaultIfEmpty(0).Max();
        using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
        using var args = cls.GetMethodParameters("CreateRestorePoint");
        args["Description"] = description;
        args["RestorePointType"] = 12; // MODIFY_SETTINGS
        args["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
        using var result = cls.InvokeMethod("CreateRestorePoint", args, null);
        int code = Convert.ToInt32(result["ReturnValue"]);
        if (code == 1058) throw RpcException.Failed(Loc.S("Sistem Koruması bu bilgisayarda kapalı. Windows'ta \"Geri yükleme noktası oluştur\" penceresinden açabilirsiniz.", "System Protection is turned off on this PC. You can turn it on in Windows' \"Create a restore point\" dialog."));
        if (code != 0) throw RpcException.Failed(Loc.S("Geri yükleme noktası oluşturulamadı (kod {0}).", "The restore point could not be created (code {0}).").Replace("{0}", code.ToString()));
        int after = Wmi.Query("SELECT SequenceNumber FROM SystemRestore", @"root\default").Select(r => r.I("SequenceNumber")).DefaultIfEmpty(0).Max();
        using var scope = journal.Begin("backup", Loc.T("Geri yükleme noktası: {0}", "Restore point: {0}").F(description));
        scope.Note("restore.point", description);
        // Windows quietly refuses a second automatic point within 24 hours and still reports success.
        return new { created = after > before, simulated = false, skipped = after <= before };
    }

    private object Export() => new
    {
        exported = DateTime.UtcNow,
        opticore = OptiCoreEngine.Version,
        machine = Environment.MachineName,
        settings = settings.Current,
        tweaksApplied = tweaks.AppliedIds(),
        startup = startup.List().Select(e => new { e.Id, e.Name, e.Kind, e.Enabled, e.Command }).ToList(),
        changes = journal.List().Select(s => new
        {
            s.Id, s.Time, s.Source, title = s.Title.Tr, s.Simulated, s.RevertedAt,
            entries = s.Entries.Select(e => new { e.Kind, e.Target, e.Before, e.After, e.Applied, e.Reverted }),
        }).ToList(),
    };

    public void Register(RpcRouter rpc)
    {
        rpc.On("backup.verify", c => Verify(c.Str("id")));
        rpc.On("backup.revertAll", _ => RevertAll());
        rpc.On("backup.restorePoints", _ => RestorePoints());
        rpc.On("backup.createRestorePoint", c => CreateRestorePoint(c.Str("description", "OptiCore")));
        rpc.On("backup.export", _ => Export());
        rpc.Do("backup.openSystemRestore", _ => Process.Start(new ProcessStartInfo(Proc.Sys("rstrui.exe")) { UseShellExecute = true }));
        rpc.Do("backup.openSystemProtection", _ => Process.Start(new ProcessStartInfo(Proc.Sys("SystemPropertiesProtection.exe")) { UseShellExecute = true }));
    }
}
