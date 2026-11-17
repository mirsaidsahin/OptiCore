using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using OptiCore.Engine.Core;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Modules;

public sealed class OsInfo
{
    public string Name { get; set; } = "";
    public string Edition { get; set; } = "";
    public string Version { get; set; } = "";
    public string Build { get; set; } = "";
    public int BuildNumber { get; set; }
    public string Arch { get; set; } = "";
    public DateTime? InstallDate { get; set; }
    public DateTime LastBoot { get; set; }
    public long UptimeSeconds { get; set; }
    public string User { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Locale { get; set; } = "";
    public bool? SecureBoot { get; set; }
    public string Firmware { get; set; } = "";
    public string? Tpm { get; set; }
    public bool FastStartup { get; set; }
}

public sealed class ComputerInfo { public string Manufacturer { get; set; } = ""; public string Model { get; set; } = ""; public string Type { get; set; } = "desktop"; public string? Sku { get; set; } public string? Serial { get; set; } }
public sealed class BoardInfo { public string Manufacturer { get; set; } = ""; public string Product { get; set; } = ""; public string Version { get; set; } = ""; public string BiosVendor { get; set; } = ""; public string BiosVersion { get; set; } = ""; public DateTime? BiosDate { get; set; } }

public sealed class CpuInfo
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public int Cores { get; set; }
    public int Threads { get; set; }
    public int PerformanceCores { get; set; }
    public int EfficiencyCores { get; set; }
    public int BaseMhz { get; set; }
    public string Socket { get; set; } = "";
    public long L2Kb { get; set; }
    public long L3Kb { get; set; }
    public bool Virtualization { get; set; }
}

public sealed class MemModule { public string Slot { get; set; } = ""; public long CapacityBytes { get; set; } public int SpeedMhz { get; set; } public int ConfiguredMhz { get; set; } public string Type { get; set; } = ""; public string FormFactor { get; set; } = ""; public string Manufacturer { get; set; } = ""; public string PartNumber { get; set; } = ""; }
public sealed class MemInfo { public long TotalBytes { get; set; } public long InstalledBytes { get; set; } public int SlotsUsed { get; set; } public int SlotsTotal { get; set; } public long MaxCapacityBytes { get; set; } public List<MemModule> Modules { get; set; } = []; }
public sealed class GpuInfo { public string Name { get; set; } = ""; public string Vendor { get; set; } = ""; public string DriverVersion { get; set; } = ""; public DateTime? DriverDate { get; set; } public long DedicatedBytes { get; set; } public long SharedBytes { get; set; } public string Resolution { get; set; } = ""; public int RefreshHz { get; set; } public bool Integrated { get; set; } }

public sealed class DiskInfo
{
    public int Index { get; set; }
    public string Model { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>SSD | HDD | Unknown</summary>
    public string Media { get; set; } = "Unknown";
    public string Bus { get; set; } = "";
    /// <summary>healthy | warning | unhealthy | unknown</summary>
    public string Health { get; set; } = "unknown";
    public string Firmware { get; set; } = "";
    public string Serial { get; set; } = "";
    public List<string> Letters { get; set; } = [];
    public bool IsSystem { get; set; }
    /// <summary>False when the SMART counters could not be read (they need administrator rights).</summary>
    public bool SmartAvailable { get; set; }
    public double? TemperatureC { get; set; }
    public double? WearPercent { get; set; }
    public long? PowerOnHours { get; set; }
    public long? ReadErrors { get; set; }
    public long? WriteErrors { get; set; }
    public long? StartStopCycles { get; set; }
}

public sealed class VolumeInfo { public string Letter { get; set; } = ""; public string Label { get; set; } = ""; public string FileSystem { get; set; } = ""; public long TotalBytes { get; set; } public long FreeBytes { get; set; } public string Kind { get; set; } = "fixed"; public bool IsSystem { get; set; } public int? DiskIndex { get; set; } public string? Media { get; set; } }
public sealed class NicInfo { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Description { get; set; } = ""; public string Type { get; set; } = ""; public string Mac { get; set; } = ""; public long SpeedBps { get; set; } public string? Ipv4 { get; set; } public string? Ipv6 { get; set; } public string? Gateway { get; set; } public List<string> Dns { get; set; } = []; public bool Dhcp { get; set; } public string Status { get; set; } = ""; public bool Physical { get; set; } }

public sealed class BatteryInfo
{
    public bool Present { get; set; }
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Chemistry { get; set; } = "";
    public long DesignMwh { get; set; }
    public long FullMwh { get; set; }
    public double? HealthPercent { get; set; }
    public int? CycleCount { get; set; }
    public int ChargePercent { get; set; }
    public bool Charging { get; set; }
    public bool OnAc { get; set; }
    /// <summary>Positive while charging, negative while discharging (milliwatts).</summary>
    public long RateMw { get; set; }
    public long VoltageMv { get; set; }
    public long RemainingMwh { get; set; }
    public int? EstimatedMinutes { get; set; }
}

public sealed class Hardware
{
    public ComputerInfo Computer { get; set; } = new();
    public BoardInfo Board { get; set; } = new();
    public CpuInfo Cpu { get; set; } = new();
    public MemInfo Memory { get; set; } = new();
    public List<GpuInfo> Gpus { get; set; } = [];
    public List<DiskInfo> Disks { get; set; } = [];
    public OsInfo Os { get; set; } = new();
}

/// <summary>Hardware and Windows inventory. Slow WMI queries run in parallel once and are cached.</summary>
public sealed class SystemInfoService
{
    private readonly object _gate = new();
    private Hardware? _hardware;
    private DateTime _hardwareAt;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr info, uint length, out uint returned, IntPtr process, uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool GetFirmwareType(out int firmwareType);

    private static bool Meaningful(string value) =>
        value.Length > 2 && value.Trim('0', ' ', '.', '-').Length > 0
        && !new[] { "O.E.M", "Default string", "To be filled", "System Product", "System Serial", "Not Applicable", "None" }.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));

    public Hardware Hardware(bool refresh = false)
    {
        lock (_gate)
        {
            if (_hardware is null || refresh || (DateTime.UtcNow - _hardwareAt).TotalMinutes > 10)
            {
                _hardware = Collect();
                _hardwareAt = DateTime.UtcNow;
            }
            RefreshUptime(_hardware.Os);
            return _hardware;
        }
    }

    private static Hardware Collect()
    {
        var hw = new Hardware();
        Parallel.Invoke(
            () => Guard("computer", () => hw.Computer = ReadComputer()),
            () => Guard("board", () => hw.Board = ReadBoard()),
            () => Guard("cpu", () => hw.Cpu = ReadCpu()),
            () => Guard("memory", () => hw.Memory = ReadMemory()),
            () => Guard("gpu", () => hw.Gpus = ReadGpus()),
            () => Guard("disks", () => hw.Disks = ReadDisks()),
            () => Guard("os", () => hw.Os = ReadOs()));
        return hw;
    }

    private static void Guard(string what, Action read)
    {
        try { read(); } catch (Exception e) { Log.Warn("system: " + what, e); }
    }

    // ------------------------------------------------------------------ Windows
    private static OsInfo ReadOs()
    {
        const string cv = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        int build = int.TryParse(Reg.GetStr(cv, "CurrentBuild"), out var b) ? b : Environment.OSVersion.Version.Build;
        string name = Reg.GetStr(cv, "ProductName") ?? "Windows";
        if (build >= 22000) name = name.Replace("Windows 10", "Windows 11"); // the registry still says "Windows 10" on Windows 11
        var os = new OsInfo
        {
            Name = name,
            Edition = Reg.GetStr(cv, "EditionID") ?? "",
            Version = Reg.GetStr(cv, "DisplayVersion") ?? Reg.GetStr(cv, "ReleaseId") ?? "",
            BuildNumber = build,
            Build = build + "." + (Reg.GetInt(cv, "UBR") ?? 0),
            Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            User = Environment.UserName,
            Machine = Environment.MachineName,
            Locale = CultureInfo.CurrentUICulture.Name,
            Firmware = GetFirmwareType(out int firmware) ? firmware == 2 ? "UEFI" : firmware == 1 ? "BIOS" : "" : "",
            SecureBoot = Reg.GetInt(@"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled") is int sb ? sb == 1 : null,
            FastStartup = Reg.GetInt(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled") == 1,
        };
        if (Reg.GetInt(cv, "InstallDate") is int install && install > 0) os.InstallDate = DateTimeOffset.FromUnixTimeSeconds((uint)install).UtcDateTime;
        // The device name is localized, the hardware id is not: MSFT0101 is the TPM 2.0 device.
        foreach (var row in Wmi.Query("SELECT Name, DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'SecurityDevices'"))
        {
            string id = row.S("DeviceID"), n = row.S("Name");
            if (id.Contains("MSFT0101", StringComparison.OrdinalIgnoreCase) || n.Contains("2.0")) os.Tpm = "2.0";
            else if (os.Tpm is null && (id.Contains("PNP0C31", StringComparison.OrdinalIgnoreCase) || n.Contains("1.2"))) os.Tpm = "1.2";
        }
        RefreshUptime(os);
        return os;
    }

    private static void RefreshUptime(OsInfo os)
    {
        os.UptimeSeconds = Environment.TickCount64 / 1000;
        os.LastBoot = DateTime.UtcNow.AddSeconds(-os.UptimeSeconds);
    }

    // ------------------------------------------------------------------ machine
    private static ComputerInfo ReadComputer()
    {
        var info = new ComputerInfo();
        foreach (var row in Wmi.Query("SELECT Manufacturer, Model, PCSystemType, SystemSKUNumber FROM Win32_ComputerSystem"))
        {
            info.Manufacturer = row.S("Manufacturer");
            info.Model = row.S("Model");
            info.Sku = Meaningful(row.S("SystemSKUNumber")) ? row.S("SystemSKUNumber") : null;
            if (row.I("PCSystemType") == 2) info.Type = "laptop";
        }
        foreach (var row in Wmi.Query("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
        {
            if (row.TryGetValue("ChassisTypes", out var v) && v is ushort[] types)
            {
                if (types.Any(t => t is 8 or 9 or 10 or 14 or 31 or 32)) info.Type = "laptop";
                else if (types.Any(t => t == 30)) info.Type = "tablet";
                else if (types.Any(t => t == 13)) info.Type = "all-in-one";
                else if (types.Any(t => t is 35 or 36)) info.Type = "mini-pc";
            }
        }
        foreach (var row in Wmi.Query("SELECT IdentifyingNumber FROM Win32_ComputerSystemProduct"))
        {
            var serial = row.S("IdentifyingNumber");
            if (Meaningful(serial)) info.Serial = serial;
        }
        return info;
    }

    private static BoardInfo ReadBoard()
    {
        var board = new BoardInfo();
        foreach (var row in Wmi.Query("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard"))
        {
            board.Manufacturer = row.S("Manufacturer");
            board.Product = row.S("Product");
            board.Version = row.S("Version");
        }
        foreach (var row in Wmi.Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
        {
            board.BiosVendor = row.S("Manufacturer");
            board.BiosVersion = row.S("SMBIOSBIOSVersion");
            board.BiosDate = row.Date("ReleaseDate");
        }
        return board;
    }

    private static CpuInfo ReadCpu()
    {
        const string key = @"HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        var cpu = new CpuInfo
        {
            Name = (Reg.GetStr(key, "ProcessorNameString") ?? "").Trim(),
            Vendor = Reg.GetStr(key, "VendorIdentifier") ?? "",
            BaseMhz = Reg.GetInt(key, "~MHz") ?? 0,
            Threads = Environment.ProcessorCount,
        };
        foreach (var row in Wmi.Query("SELECT NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, VirtualizationFirmwareEnabled FROM Win32_Processor"))
        {
            cpu.Cores += row.I("NumberOfCores");
            // Windows' "base speed". On hybrid CPUs it differs from the TSC frequency stored in the registry.
            if (row.I("MaxClockSpeed") > 0) cpu.BaseMhz = row.I("MaxClockSpeed");
            cpu.L2Kb = row.L("L2CacheSize");
            cpu.L3Kb = row.L("L3CacheSize");
            cpu.Socket = row.S("SocketDesignation");
            cpu.Virtualization = row.B("VirtualizationFirmwareEnabled");
        }
        ReadCoreClasses(cpu);
        if (cpu.Cores == 0) cpu.Cores = cpu.Threads;
        return cpu;
    }

    /// <summary>Counts performance and efficiency cores on hybrid CPUs (Intel 12th gen and later, Snapdragon).</summary>
    private static void ReadCoreClasses(CpuInfo cpu)
    {
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint needed, IntPtr.Zero, 0);
        if (needed == 0) return;
        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, needed, out uint returned, IntPtr.Zero, 0)) return;
            var classes = new Dictionary<(int Group, int Core), int>();
            for (int offset = 0; offset < returned;)
            {
                int size = Marshal.ReadInt32(buffer, offset);
                if (size <= 0) break;
                int group = (ushort)Marshal.ReadInt16(buffer, offset + 12);
                int core = Marshal.ReadByte(buffer, offset + 15);
                classes[(group, core)] = Marshal.ReadByte(buffer, offset + 18);
                offset += size;
            }
            var distinct = classes.Values.Distinct().OrderBy(x => x).ToList();
            if (distinct.Count >= 2)
            {
                cpu.PerformanceCores = classes.Values.Count(v => v == distinct[^1]);
                cpu.EfficiencyCores = classes.Count - cpu.PerformanceCores;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static MemInfo ReadMemory()
    {
        var mem = new MemInfo { TotalBytes = (long)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes };
        foreach (var row in Wmi.Query("SELECT DeviceLocator, BankLabel, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, FormFactor, Manufacturer, PartNumber FROM Win32_PhysicalMemory"))
        {
            mem.Modules.Add(new MemModule
            {
                Slot = row.S("DeviceLocator") is { Length: > 0 } loc ? loc : row.S("BankLabel"),
                CapacityBytes = row.L("Capacity"),
                SpeedMhz = row.I("Speed"),
                ConfiguredMhz = row.I("ConfiguredClockSpeed"),
                Type = row.I("SMBIOSMemoryType") switch { 20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => "" },
                FormFactor = row.I("FormFactor") switch { 8 => "DIMM", 12 => "SO-DIMM", 11 => "Row of chips", _ => "" },
                Manufacturer = row.S("Manufacturer"),
                PartNumber = row.S("PartNumber"),
            });
        }
        mem.InstalledBytes = mem.Modules.Sum(m => m.CapacityBytes);
        mem.SlotsUsed = mem.Modules.Count;
        foreach (var row in Wmi.Query("SELECT MemoryDevices, MaxCapacity, MaxCapacityEx FROM Win32_PhysicalMemoryArray"))
        {
            mem.SlotsTotal += row.I("MemoryDevices");
            long kb = row.L("MaxCapacityEx") > 0 ? row.L("MaxCapacityEx") : row.L("MaxCapacity");
            mem.MaxCapacityBytes += kb * 1024;
        }
        if (mem.SlotsTotal < mem.SlotsUsed) mem.SlotsTotal = mem.SlotsUsed;
        if (mem.InstalledBytes == 0) mem.InstalledBytes = mem.TotalBytes;
        return mem;
    }

    private static List<GpuInfo> ReadGpus()
    {
        var adapters = GpuProbe.Adapters();
        var list = new List<GpuInfo>();
        foreach (var row in Wmi.Query("SELECT Name, AdapterCompatibility, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, PNPDeviceID FROM Win32_VideoController"))
        {
            string name = row.S("Name");
            if (row.S("PNPDeviceID").StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) continue; // virtual display adapters
            var probe = adapters.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? adapters.FirstOrDefault(a => a.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            var gpu = new GpuInfo
            {
                Name = name, Vendor = row.S("AdapterCompatibility"), DriverVersion = row.S("DriverVersion"), DriverDate = row.Date("DriverDate"),
                DedicatedBytes = probe?.DedicatedBytes ?? 0, SharedBytes = probe?.SharedBytes ?? 0,
                RefreshHz = row.I("CurrentRefreshRate"),
                Resolution = row.I("CurrentHorizontalResolution") > 0 ? $"{row.I("CurrentHorizontalResolution")}x{row.I("CurrentVerticalResolution")}" : "",
            };
            gpu.Integrated = gpu.DedicatedBytes < 768L << 20;
            list.Add(gpu);
        }
        return list;
    }

    private static List<DiskInfo> ReadDisks()
    {
        const string scope = @"root\Microsoft\Windows\Storage";
        var disks = new List<DiskInfo>();
        foreach (var row in Wmi.Query("SELECT DeviceId, FriendlyName, Size, MediaType, BusType, HealthStatus, FirmwareVersion, SerialNumber FROM MSFT_PhysicalDisk", scope))
        {
            disks.Add(new DiskInfo
            {
                Index = int.TryParse(row.S("DeviceId"), out var idx) ? idx : disks.Count,
                Model = row.S("FriendlyName"),
                SizeBytes = row.L("Size"),
                Media = row.I("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unknown" },
                Bus = row.I("BusType") switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 10 => "SAS", 3 => "ATA", 8 => "RAID", 12 => "SD", 13 => "MMC", 1 => "SCSI", 16 => "Storage Spaces", _ => "" },
                Health = row.I("HealthStatus") switch { 0 => "healthy", 1 => "warning", 2 => "unhealthy", _ => "unknown" },
                Firmware = row.S("FirmwareVersion"),
                Serial = row.S("SerialNumber"),
            });
        }
        if (disks.Count == 0)
        {
            foreach (var row in Wmi.Query("SELECT Index, Model, Size, InterfaceType, FirmwareRevision, SerialNumber FROM Win32_DiskDrive"))
                disks.Add(new DiskInfo { Index = row.I("Index"), Model = row.S("Model"), SizeBytes = row.L("Size"), Bus = row.S("InterfaceType"), Firmware = row.S("FirmwareRevision"), Serial = row.S("SerialNumber") });
        }
        foreach (var d in disks.Where(d => d.Media == "Unknown" && d.Bus == "NVMe")) d.Media = "SSD";

        string systemLetter = Paths.SystemDrive[..1];
        foreach (var row in Wmi.Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", scope))
        {
            if (!row.TryGetValue("DriveLetter", out var raw) || raw is null) continue;
            char letter = raw is char ch ? ch : Convert.ToChar(Convert.ToUInt16(raw, CultureInfo.InvariantCulture));
            if (!char.IsLetter(letter)) continue;
            var disk = disks.FirstOrDefault(d => d.Index == row.I("DiskNumber"));
            if (disk is null) continue;
            disk.Letters.Add(letter.ToString());
            if (letter.ToString().Equals(systemLetter, StringComparison.OrdinalIgnoreCase)) disk.IsSystem = true;
        }

        // SMART-style reliability counters: Windows only hands these to administrators.
        var counters = Wmi.Query("SELECT DeviceId, Temperature, Wear, PowerOnHours, ReadErrorsTotal, WriteErrorsTotal, StartStopCycleCount FROM MSFT_StorageReliabilityCounter", scope, out var error);
        if (error is null)
            foreach (var row in counters)
            {
                var disk = disks.FirstOrDefault(d => d.Index.ToString(CultureInfo.InvariantCulture) == row.S("DeviceId"));
                if (disk is null) continue;
                disk.SmartAvailable = true;
                if (row.Has("Temperature") && row.I("Temperature") is > 0 and < 120) disk.TemperatureC = row.I("Temperature");
                if (row.Has("Wear")) disk.WearPercent = row.I("Wear");
                if (row.Has("PowerOnHours")) disk.PowerOnHours = row.L("PowerOnHours");
                if (row.Has("ReadErrorsTotal")) disk.ReadErrors = row.L("ReadErrorsTotal");
                if (row.Has("WriteErrorsTotal")) disk.WriteErrors = row.L("WriteErrorsTotal");
                if (row.Has("StartStopCycleCount")) disk.StartStopCycles = row.L("StartStopCycleCount");
            }
        return [.. disks.OrderBy(d => d.Index)];
    }

    // ------------------------------------------------------------------ cheap, always fresh
    public List<VolumeInfo> Volumes()
    {
        var disks = _hardware?.Disks ?? [];
        var list = new List<VolumeInfo>();
        string system = Paths.SystemDrive;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.NoRootDirectory) continue;
                string letter = d.Name[..1];
                var disk = disks.FirstOrDefault(x => x.Letters.Contains(letter, StringComparer.OrdinalIgnoreCase));
                list.Add(new VolumeInfo
                {
                    Letter = letter, Label = d.VolumeLabel, FileSystem = d.DriveFormat, TotalBytes = d.TotalSize, FreeBytes = d.AvailableFreeSpace,
                    Kind = d.DriveType switch { DriveType.Removable => "removable", DriveType.Network => "network", DriveType.Ram => "ram", _ => "fixed" },
                    IsSystem = d.Name.Equals(system, StringComparison.OrdinalIgnoreCase), DiskIndex = disk?.Index, Media = disk?.Media,
                });
            }
            catch { /* drive went away */ }
        }
        return list;
    }

    public static List<NicInfo> Adapters()
    {
        var list = new List<NicInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (!NetAdapters.IsReal(nic)) continue;
                var props = nic.GetIPProperties();
                var info = new NicInfo
                {
                    Id = nic.Id, Name = nic.Name, Description = nic.Description,
                    Type = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi" : nic.NetworkInterfaceType.ToString().Contains("Ethernet") ? "ethernet" : nic.NetworkInterfaceType.ToString().ToLowerInvariant(),
                    Mac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(x => x.ToString("X2"))),
                    SpeedBps = nic.OperationalStatus == OperationalStatus.Up && nic.Speed > 0 ? nic.Speed : 0,
                    Status = nic.OperationalStatus == OperationalStatus.Up ? "up" : "down",
                    Ipv4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString(),
                    Ipv6 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal)?.Address.ToString(),
                    Gateway = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString(),
                    Dns = [.. props.DnsAddresses.Where(a => !a.IsIPv6SiteLocal).Select(a => a.ToString())],
                };
                try { info.Dhcp = props.GetIPv4Properties()?.IsDhcpEnabled ?? false; } catch { /* no IPv4 on this adapter */ }
                info.Physical = NetAdapters.IsPhysical(nic);
                list.Add(info);
            }
            catch { /* adapter changed while reading */ }
        }
        return [.. list.OrderByDescending(n => n.Status == "up").ThenByDescending(n => n.Physical)];
    }

    // ------------------------------------------------------------------ battery
    public static BatteryInfo Battery()
    {
        var info = new BatteryInfo();
        foreach (var row in Wmi.Query("SELECT Name, EstimatedChargeRemaining, BatteryStatus, Chemistry, EstimatedRunTime FROM Win32_Battery"))
        {
            info.Present = true;
            info.Name = row.S("Name");
            info.ChargePercent = row.I("EstimatedChargeRemaining");
            info.Chemistry = row.I("Chemistry") switch { 3 => "Lead acid", 4 => "NiCd", 5 => "NiMH", 6 => "Li-ion", 8 => "Li-polymer", _ => "" };
            int minutes = row.I("EstimatedRunTime");
            if (minutes is > 0 and < 5000) info.EstimatedMinutes = minutes;
        }
        if (!info.Present) return info;

        const string scope = @"root\WMI";
        foreach (var row in Wmi.Query("SELECT DesignedCapacity, ManufactureName, DeviceName FROM BatteryStaticData", scope, out _))
        {
            info.DesignMwh = row.L("DesignedCapacity");
            info.Manufacturer = row.S("ManufactureName");
            if (info.Name.Length == 0) info.Name = row.S("DeviceName");
        }
        foreach (var row in Wmi.Query("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", scope, out _)) info.FullMwh = row.L("FullChargedCapacity");
        foreach (var row in Wmi.Query("SELECT CycleCount FROM BatteryCycleCount", scope, out _)) if (row.I("CycleCount") > 0) info.CycleCount = row.I("CycleCount");
        foreach (var row in Wmi.Query("SELECT PowerOnline, Charging, ChargeRate, DischargeRate, RemainingCapacity, Voltage FROM BatteryStatus", scope, out _))
        {
            info.OnAc = row.B("PowerOnline");
            info.Charging = row.B("Charging");
            info.RateMw = row.L("ChargeRate") > 0 ? row.L("ChargeRate") : -row.L("DischargeRate");
            info.RemainingMwh = row.L("RemainingCapacity");
            info.VoltageMv = row.L("Voltage");
        }
        if (info.DesignMwh == 0 || info.FullMwh == 0) FillFromReport(info);
        if (info.DesignMwh > 0 && info.FullMwh > 0) info.HealthPercent = Math.Min(100, 100.0 * info.FullMwh / info.DesignMwh);
        if (info.EstimatedMinutes is null && info.RateMw < -500 && info.RemainingMwh > 0) info.EstimatedMinutes = (int)(info.RemainingMwh * 60.0 / -info.RateMw);
        return info;
    }

    /// <summary>Windows' own battery report: capacity history week by week, straight from the firmware counters.</summary>
    public static (XDocument? Report, string? Error) BatteryReport()
    {
        string file = Path.Combine(Path.GetTempPath(), $"opticore-battery-{Guid.NewGuid():N}.xml");
        try
        {
            var r = Proc.Run(Proc.Sys("powercfg.exe"), $"/batteryreport /xml /output \"{file}\"", 30000);
            if (!File.Exists(file)) return (null, r.Text);
            return (XDocument.Load(file), null);
        }
        catch (Exception e) { return (null, e.Message); }
        finally { try { File.Delete(file); } catch { /* temp file */ } }
    }

    private static void FillFromReport(BatteryInfo info)
    {
        var (doc, _) = BatteryReport();
        var battery = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Battery");
        if (battery is null) return;
        long Value(string name) => long.TryParse(battery.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value, out var v) ? v : 0;
        if (info.DesignMwh == 0) info.DesignMwh = Value("DesignCapacity");
        if (info.FullMwh == 0) info.FullMwh = Value("FullChargeCapacity");
        if (info.CycleCount is null && Value("CycleCount") > 0) info.CycleCount = (int)Value("CycleCount");
        if (info.Manufacturer.Length == 0) info.Manufacturer = battery.Elements().FirstOrDefault(e => e.Name.LocalName == "Manufacturer")?.Value ?? "";
    }

    public static List<object> BatteryHistory()
    {
        var (doc, _) = BatteryReport();
        var result = new List<object>();
        if (doc is null) return result;
        foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName == "HistoryEntry"))
        {
            long full = long.TryParse(entry.Attribute("FullChargeCapacity")?.Value, out var f) ? f : 0;
            long design = long.TryParse(entry.Attribute("DesignCapacity")?.Value, out var d) ? d : 0;
            string? date = entry.Attribute("LocalStartDate")?.Value ?? entry.Attribute("StartDate")?.Value;
            if (full > 0 && design > 0 && date is not null) result.Add(new { date, fullMwh = full, designMwh = design, health = Math.Min(100, 100.0 * full / design) });
        }
        return result;
    }

    public void Register(RpcRouter rpc)
    {
        rpc.On("system.hardware", c => Hardware(c.Bool("refresh")));
        rpc.On("system.volumes", _ => { Hardware(); return Volumes(); });
        rpc.On("system.adapters", _ => Adapters());
        rpc.On("system.battery", _ => Battery());
        rpc.On("system.batteryHistory", _ => BatteryHistory());
    }
}
