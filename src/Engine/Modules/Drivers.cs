using OptiCore.Engine.Core;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public sealed class DriverInfo
{
    public string DeviceId { get; set; } = "";
    public string Device { get; set; } = "";
    public string Class { get; set; } = "";
    public string? Provider { get; set; }
    public string? Manufacturer { get; set; }
    public string? Version { get; set; }
    public DateTime? Date { get; set; }
    public string? Inf { get; set; }
    public bool Signed { get; set; }
    public int ErrorCode { get; set; }
    public LText? Error { get; set; }
    public bool Microsoft { get; set; }
    public int AgeMonths { get; set; }
}

/// <summary>
/// Driver Manager: inventory, problem devices, old drivers, backup with pnputil, and links to official vendor pages.
/// OptiCore never downloads drivers itself: a wrong driver can stop a PC from booting.
/// </summary>
public sealed class DriverService(JobManager jobs, DataStore store)
{
    private static readonly Dictionary<int, (LText Text, bool Missing)> Codes = new()
    {
        [1] = (Loc.T("Aygıt doğru yapılandırılmamış.", "The device is not configured correctly."), false),
        [3] = (Loc.T("Sürücü bozuk ya da sistemin belleği yetersiz.", "The driver is corrupt or the system is low on memory."), false),
        [10] = (Loc.T("Aygıt başlatılamıyor.", "The device cannot start."), false),
        [12] = (Loc.T("Aygıt için yeterli boş kaynak yok.", "Not enough free resources for the device."), false),
        [18] = (Loc.T("Sürücülerin yeniden yüklenmesi gerekiyor.", "The drivers need to be reinstalled."), true),
        [19] = (Loc.T("Kayıt defterindeki yapılandırma bilgisi bozuk.", "Configuration information in the registry is damaged."), false),
        [22] = (Loc.T("Aygıt devre dışı bırakılmış.", "The device is disabled."), false),
        [24] = (Loc.T("Aygıt mevcut değil ya da düzgün çalışmıyor.", "The device is not present or not working properly."), false),
        [28] = (Loc.T("Sürücü yüklü değil.", "The driver is not installed."), true),
        [31] = (Loc.T("Windows sürücüyü yükleyemedi.", "Windows could not load the driver."), true),
        [32] = (Loc.T("Sürücü hizmeti devre dışı.", "The driver service is disabled."), false),
        [37] = (Loc.T("Sürücü başlatılamadı.", "The driver failed to initialize."), false),
        [39] = (Loc.T("Sürücü bozuk ya da eksik.", "The driver is corrupt or missing."), true),
        [43] = (Loc.T("Aygıt bir sorun bildirdiği için Windows onu durdurdu.", "Windows stopped the device because it reported a problem."), false),
        [45] = (Loc.T("Aygıt şu an bağlı değil.", "The device is not connected right now."), false),
        [52] = (Loc.T("Sürücünün dijital imzası doğrulanamadı.", "The driver's digital signature could not be verified."), false),
    };

    private static readonly (string Match, string Url)[] Vendors =
    [
        ("NVIDIA", "https://www.nvidia.com/Download/index.aspx"), ("AMD", "https://www.amd.com/support/download/drivers.html"), ("Advanced Micro Devices", "https://www.amd.com/support/download/drivers.html"),
        ("Intel", "https://www.intel.com/content/www/us/en/support/detect.html"), ("Realtek", "https://www.realtek.com/Download/List?cate_id=593"),
        ("MediaTek", "https://www.mediatek.com/products/connectivity-and-networking"), ("Qualcomm", "https://www.qualcomm.com/support"),
        ("Broadcom", "https://www.broadcom.com/support/download-search"), ("Logitech", "https://support.logi.com/hc/downloads"),
        ("Acer", "https://www.acer.com/support"), ("ASUS", "https://www.asus.com/support/download-center/"), ("Lenovo", "https://pcsupport.lenovo.com/"),
        ("HP", "https://support.hp.com/drivers"), ("Dell", "https://www.dell.com/support/home"), ("MSI", "https://www.msi.com/support/download"),
        ("Gigabyte", "https://www.gigabyte.com/Support"), ("Samsung", "https://semiconductor.samsung.com/consumer-storage/support/tools/"),
    ];

    private List<DriverInfo>? _cache;

    public List<DriverInfo> List(bool refresh = false)
    {
        if (!refresh && _cache is not null) return _cache;
        var errors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Wmi.Query("SELECT PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
            errors[row.S("PNPDeviceID")] = row.I("ConfigManagerErrorCode");

        var list = new List<DriverInfo>();
        foreach (var row in Wmi.Query("SELECT DeviceID, DeviceName, DeviceClass, DriverProviderName, Manufacturer, DriverVersion, DriverDate, InfName, IsSigned FROM Win32_PnPSignedDriver"))
        {
            string name = row.S("DeviceName");
            if (name.Length == 0) continue;
            var d = new DriverInfo
            {
                DeviceId = row.S("DeviceID"), Device = name, Class = row.S("DeviceClass"), Provider = row.S("DriverProviderName"),
                Manufacturer = row.S("Manufacturer"), Version = row.S("DriverVersion"), Date = row.Date("DriverDate"), Inf = row.S("InfName"), Signed = row.B("IsSigned"),
            };
            d.Microsoft = (d.Provider ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
            if (d.Date is DateTime dt && dt.Year > 2000) d.AgeMonths = (int)((DateTime.UtcNow - dt).TotalDays / 30.4);
            if (errors.TryGetValue(d.DeviceId, out int code)) { d.ErrorCode = code; d.Error = Codes.TryGetValue(code, out var c) ? c.Text : Loc.T("Hata kodu {0}", "Error code {0}").F(code); }
            list.Add(d);
        }
        // Devices with problems and no driver at all do not appear in Win32_PnPSignedDriver.
        foreach (var row in Wmi.Query("SELECT PNPDeviceID, Name, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode = 28 OR ConfigManagerErrorCode = 1"))
        {
            string id = row.S("PNPDeviceID");
            if (list.Any(d => d.DeviceId.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
            int code = row.I("ConfigManagerErrorCode");
            list.Add(new DriverInfo { DeviceId = id, Device = row.S("Name") is { Length: > 0 } n ? n : id, Class = row.S("PNPClass"), ErrorCode = code, Error = Codes.GetValueOrDefault(code).Text });
        }
        _cache = list;
        return list;
    }

    private static readonly HashSet<string> Important = new(StringComparer.OrdinalIgnoreCase) { "DISPLAY", "NET", "MEDIA", "SCSIADAPTER", "HDC", "BLUETOOTH", "SYSTEM", "USB", "BIOMETRIC", "CAMERA" };

    private object Backup(JobContext job)
    {
        Elevation.Require();
        string dir = store.DirOf("drivers", "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        job.Report(-1, Loc.T("Üçüncü taraf sürücüler dışa aktarılıyor…", "Exporting third-party drivers…"));
        var r = Proc.Run(Proc.Sys("pnputil.exe"), $"/export-driver * \"{dir}\"", 15 * 60 * 1000);
        if (!r.Ok) throw RpcException.Failed(r.Text);
        long size = 0; int infs = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) { size += f.Length; if (f.Extension.Equals(".inf", StringComparison.OrdinalIgnoreCase)) infs++; }
        return new { path = dir, bytes = size, drivers = infs };
    }

    private object Backups() => Directory.Exists(Path.Combine(store.Root, "drivers"))
        ? Directory.EnumerateDirectories(Path.Combine(store.Root, "drivers"), "backup-*").OrderByDescending(d => d).Select(d =>
        {
            long size = 0; int infs = 0;
            foreach (var f in new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories)) { size += f.Length; if (f.Extension.Equals(".inf", StringComparison.OrdinalIgnoreCase)) infs++; }
            return new { path = d, time = Directory.GetCreationTimeUtc(d), bytes = size, drivers = infs };
        }).ToList<object>()
        : [];

    private object Restore(string path, JobContext job)
    {
        Elevation.Require();
        if (!Directory.Exists(path) || !Path.GetFullPath(path).StartsWith(Path.Combine(store.Root, "drivers"), StringComparison.OrdinalIgnoreCase))
            throw RpcException.Denied("Unknown backup");
        job.Report(-1, Loc.T("Sürücüler yükleniyor…", "Installing drivers…"));
        var r = Proc.Run(Proc.Sys("pnputil.exe"), $"/add-driver \"{Path.Combine(path, "*.inf")}\" /subdirs /install", 30 * 60 * 1000);
        return new { ok = r.ExitCode is 0 or 3010 or 259, reboot = r.ExitCode == 3010, output = r.Text.Length > 4000 ? r.Text[^4000..] : r.Text };
    }

    public IEnumerable<Finding> Findings()
    {
        var problems = List().Where(d => d.ErrorCode != 0 && d.ErrorCode is not 22 and not 45).ToList();
        if (problems.Count > 0)
            yield return new Finding
            {
                Id = "drivers.problems", Group = "stability", Severity = problems.Any(p => Important.Contains(p.Class)) ? Severity.Warning : Severity.Info, Penalty = 8,
                Title = Loc.T("{0} aygıt düzgün çalışmıyor", "{0} devices are not working properly").F(problems.Count),
                Detail = Loc.T("Aygıt Yöneticisi bu aygıtlar için hata bildiriyor; çoğu zaman eksik ya da bozuk bir sürücü anlamına gelir.", "Device Manager reports errors for these devices; usually a missing or broken driver."),
            }.Ev(Loc.T("Aygıtlar", "Devices"), string.Join(", ", problems.Take(4).Select(p => p.Device))).Go(Loc.T("Sürücüleri aç", "Open drivers"), "drivers");
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("drivers.list", c =>
        {
            var items = List(c.Bool("refresh"));
            var vendors = items.Where(d => !d.Microsoft && d.Provider is { Length: > 0 }).Select(d => d.Provider!).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => new { provider = p, url = Vendors.FirstOrDefault(v => p.Contains(v.Match, StringComparison.OrdinalIgnoreCase)).Url }).Where(v => v.url is not null).ToList();
            return new
            {
                items, vendors,
                problems = items.Count(d => d.ErrorCode != 0), old = items.Count(d => !d.Microsoft && d.AgeMonths > 36 && Important.Contains(d.Class)),
                unsigned = items.Count(d => !d.Signed && d.Version is not null),
            };
        });
        rpc.On("drivers.backup", _ => jobs.Start("drivers.backup", Loc.T("Sürücü yedekleme", "Driver backup"), Backup));
        rpc.On("drivers.backups", _ => Backups());
        rpc.On("drivers.restore", c => { string p = c.Str("path"); return jobs.Start("drivers.restore", Loc.T("Sürücü geri yükleme", "Driver restore"), job => Restore(p, job)); });
        rpc.Do("drivers.deviceManager", _ => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Proc.Sys("devmgmt.msc")) { UseShellExecute = true }));
    }
}
