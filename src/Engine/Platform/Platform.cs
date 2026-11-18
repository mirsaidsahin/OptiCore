using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using OptiCore.Engine.Core;
using OptiCore.Engine.Native;

namespace OptiCore.Engine.Platform;

public static class Elevation
{
    private static readonly Lazy<bool> Elevated = new(() =>
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    });

    public static bool IsElevated => Elevated.Value;
    public static void Require() { if (!IsElevated) throw RpcException.Elevation(); }
}

/// <summary>Runs system tools with a timeout and the correct console code page. Tools are always addressed by absolute path.</summary>
public static class Proc
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
        public string Text => (StdOut + "\n" + StdErr).Trim();
    }

    private static readonly Lazy<Encoding> Oem = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch { return Encoding.UTF8; }
    });

    public static string Sys(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public static Result Run(string file, string args, int timeoutMs = 30000, Encoding? encoding = null)
    {
        var enc = encoding ?? Oem.Value;
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = enc, StandardErrorEncoding = enc,
        };
        var so = new StringBuilder();
        var se = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (so) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (se) se.AppendLine(e.Data); };
        try { p.Start(); }
        catch (Exception e) { return new Result(-1, "", e.Message); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new Result(-2, so.ToString(), "timeout");
        }
        p.WaitForExit();
        return new Result(p.ExitCode, so.ToString(), se.ToString());
    }

    /// <summary>Runs a short PowerShell 5.1 script; output is UTF-8.</summary>
    public static Result PowerShell(string script, int timeoutMs = 60000)
    {
        var full = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script;
        var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return Run(Sys(@"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + b64, timeoutMs, Encoding.UTF8);
    }
}

/// <summary>
/// Registry access by path ("HKCU\Software\..."). When <see cref="SandboxRoot"/> is set (tests only),
/// every path is transparently redirected below that key, so nothing real is touched.
/// </summary>
public static class Reg
{
    public static string? SandboxRoot { get; set; }

    private static string Short(string hive) => hive.ToUpperInvariant() switch
    {
        "HKCU" or "HKEY_CURRENT_USER" => "HKCU",
        "HKLM" or "HKEY_LOCAL_MACHINE" => "HKLM",
        "HKCR" or "HKEY_CLASSES_ROOT" => "HKCR",
        "HKU" or "HKEY_USERS" => "HKU",
        "HKCC" or "HKEY_CURRENT_CONFIG" => "HKCC",
        _ => throw new ArgumentException("Unknown registry hive: " + hive),
    };

    public static string Canonical(string path)
    {
        int i = path.IndexOf('\\');
        return i < 0 ? Short(path) : Short(path[..i]) + path[i..];
    }

    public static (RegistryHive Hive, string Sub) Parse(string path)
    {
        path = Canonical(path);
        if (SandboxRoot is not null && !path.StartsWith(SandboxRoot, StringComparison.OrdinalIgnoreCase))
            path = SandboxRoot + "\\" + path;
        int i = path.IndexOf('\\');
        string sub = i < 0 ? "" : path[(i + 1)..];
        var hive = (i < 0 ? path : path[..i]) switch
        {
            "HKCU" => RegistryHive.CurrentUser,
            "HKLM" => RegistryHive.LocalMachine,
            "HKCR" => RegistryHive.ClassesRoot,
            "HKU" => RegistryHive.Users,
            _ => RegistryHive.CurrentConfig,
        };
        return (hive, sub);
    }

    /// <summary>Opens a key. Read-only opens return null when the key is missing or unreadable; writable opens throw.</summary>
    public static RegistryKey? Open(string path, bool writable = false, RegistryView view = RegistryView.Registry64)
    {
        var (hive, sub) = Parse(path);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            return sub.Length == 0 ? RegistryKey.OpenBaseKey(hive, view) : root.OpenSubKey(sub, writable);
        }
        catch (Exception e) when (!writable && e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static RegistryKey Create(string path, RegistryView view = RegistryView.Registry64)
    {
        var (hive, sub) = Parse(path);
        using var root = RegistryKey.OpenBaseKey(hive, view);
        return root.CreateSubKey(sub, writable: true) ?? throw new IOException("Cannot create registry key " + path);
    }

    public static object? Get(string path, string? name, RegistryView view = RegistryView.Registry64)
    {
        using var k = Open(path, false, view);
        try { return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); }
        catch { return null; }
    }

    public static int? GetInt(string path, string name, RegistryView view = RegistryView.Registry64) => Get(path, name, view) switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    public static string? GetStr(string path, string? name, RegistryView view = RegistryView.Registry64) => Get(path, name, view) switch
    {
        null => null,
        string s => s,
        string[] a => string.Join("\n", a),
        var o => Convert.ToString(o, CultureInfo.InvariantCulture),
    };

    public static string[] SubKeys(string path, RegistryView view = RegistryView.Registry64)
    {
        using var k = Open(path, false, view);
        try { return k?.GetSubKeyNames() ?? []; } catch { return []; }
    }

    public static string[] ValueNames(string path, RegistryView view = RegistryView.Registry64)
    {
        using var k = Open(path, false, view);
        try { return k?.GetValueNames() ?? []; } catch { return []; }
    }

    public static bool Exists(string path, RegistryView view = RegistryView.Registry64)
    {
        using var k = Open(path, false, view);
        return k is not null;
    }
}

public sealed class WmiRow() : Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
{
    public string S(string key) =>
        TryGetValue(key, out var v) && v is not null ? (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Trim() : "";

    public long L(string key)
    {
        try { return TryGetValue(key, out var v) && v is not null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0; }
        catch { return 0; }
    }

    public int I(string key) => (int)Math.Clamp(L(key), int.MinValue, int.MaxValue);

    public double D(string key)
    {
        try { return TryGetValue(key, out var v) && v is not null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0; }
        catch { return 0; }
    }

    public bool B(string key) => TryGetValue(key, out var v) && v is bool b && b;
    public bool Has(string key) => TryGetValue(key, out var v) && v is not null;
    public string[] Arr(string key) => TryGetValue(key, out var v) && v is string[] a ? a : [];

    public DateTime? Date(string key)
    {
        var s = S(key);
        if (s.Length < 14) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s).ToUniversalTime(); } catch { return null; }
    }
}

public static class Wmi
{
    public static List<WmiRow> Query(string wql, string scope = @"root\cimv2", int timeoutSec = 30) => Query(wql, scope, out _, timeoutSec);

    /// <param name="error">null on success, "denied" when admin rights are needed, otherwise "unavailable".</param>
    public static List<WmiRow> Query(string wql, string scope, out string? error, int timeoutSec = 30, bool quiet = false)
    {
        var rows = new List<WmiRow>();
        error = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            searcher.Options.Timeout = TimeSpan.FromSeconds(timeoutSec);
            searcher.Options.ReturnImmediately = true;
            searcher.Options.Rewindable = false;
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                using (mo)
                {
                    var row = new WmiRow();
                    foreach (var p in mo.Properties) row[p.Name] = p.Value;
                    rows.Add(row);
                }
            }
        }
        catch (ManagementException e) when (e.ErrorCode == ManagementStatus.AccessDenied) { error = "denied"; }
        catch (UnauthorizedAccessException) { error = "denied"; }
        catch (Exception e)
        {
            error = "unavailable";
            if (!quiet) Log.Warn($"wmi: {scope} :: {wql}", e);
        }
        return rows;
    }
}

/// <summary>Performance counters through PDH with English counter paths, so they work on any Windows display language.</summary>
public sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = [];

    public PdhQuery()
    {
        if (Win32.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
    }

    public bool Add(string key, string englishPath)
    {
        if (_query == IntPtr.Zero) return false;
        if (Win32.PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out var counter) != 0) return false;
        _counters[key] = counter;
        return true;
    }

    public bool Has(string key) => _counters.ContainsKey(key);
    public bool Collect() => _query != IntPtr.Zero && Win32.PdhCollectQueryData(_query) == 0;

    public double? Value(string key)
    {
        if (!_counters.TryGetValue(key, out var c)) return null;
        int hr = Win32.PdhGetFormattedCounterValue(c, Win32.PDH_FMT_DOUBLE | Win32.PDH_FMT_NOCAP100, IntPtr.Zero, out var v);
        return hr == 0 && v.CStatus is 0 or 1 && double.IsFinite(v.DoubleValue) ? v.DoubleValue : null;
    }

    /// <summary>Reads every instance of a wildcard counter. Instances sharing a name are summed.</summary>
    public Dictionary<string, double> Array(string key)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!_counters.TryGetValue(key, out var c)) return result;
        const uint format = Win32.PDH_FMT_DOUBLE | Win32.PDH_FMT_NOCAP100;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            int hr = Win32.PdhGetFormattedCounterArrayW(c, format, ref size, out _, IntPtr.Zero);
            if (hr != Win32.PDH_MORE_DATA || size == 0) return result;
            size += 4096; // instances may appear between the two calls
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                hr = Win32.PdhGetFormattedCounterArrayW(c, format, ref size, out uint count, buffer);
                if (hr == Win32.PDH_MORE_DATA) continue;
                if (hr != 0) return result;
                int itemSize = IntPtr.Size + 16;
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = buffer + i * itemSize;
                    uint status = (uint)Marshal.ReadInt32(item, IntPtr.Size);
                    if (status > 1) continue;
                    string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                    double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                    if (name is null || !double.IsFinite(value)) continue;
                    result[name] = result.TryGetValue(name, out var existing) ? existing + value : value;
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { Win32.PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }
}

/// <summary>Authenticode check for executables: is it signed, by whom, and does Windows trust the chain.</summary>
public static class Signature
{
    public sealed record Info(bool Signed, bool Trusted, string? Signer);
    private static readonly Info None = new(false, false, null);
    private static readonly ConcurrentDictionary<string, Info> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Info Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return None;
        return Cache.GetOrAdd(path, Verify);
    }

    private static Info Verify(string path)
    {
        try
        {
            if (!File.Exists(path)) return None;
            string? signer = null;
#pragma warning disable SYSLIB0057
            try { using var cert = X509Certificate.CreateFromSignedFile(path); signer = CommonName(cert.Subject); }
            catch { /* no embedded signature */ }
#pragma warning restore SYSLIB0057
            if (signer is null) return None;
            return new Info(true, WinVerify(path) == 0, signer);
        }
        catch { return None; }
    }

    private static string CommonName(string subject)
    {
        foreach (var part in subject.Split(','))
        {
            var t = part.Trim();
            if (t.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return t[3..].Trim('"');
        }
        return subject;
    }

    private static int WinVerify(string path)
    {
        var fileInfo = new Win32.WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<Win32.WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<Win32.WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new Win32.WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<Win32.WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = pFile,
                dwProvFlags = 0x1000 | 0x10, // cache-only URL retrieval, no revocation check: never goes online
            };
            var action = Win32.WINTRUST_ACTION_GENERIC_VERIFY_V2;
            return Win32.WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<Win32.WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}

/// <summary>File icons as PNG data URLs, cached per path. The UI fetches them once and keeps them.</summary>
public static class Icons
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? DataUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return Cache.GetOrAdd(path, static p =>
        {
            try
            {
                if (!File.Exists(p)) return null;
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(p);
                if (icon is null) return null;
                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
            catch { return null; }
        });
    }
}

public static class ShellOps
{
    public static (long Bytes, long Items) RecycleBinInfo()
    {
        var info = new Win32.SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<Win32.SHQUERYRBINFO>() };
        if (Win32.SHQueryRecycleBinW(null, ref info) == 0) return (info.i64Size, info.i64NumItems);
        long bytes = 0, items = 0;
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var one = new Win32.SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<Win32.SHQUERYRBINFO>() };
            if (Win32.SHQueryRecycleBinW(d.Name, ref one) == 0) { bytes += one.i64Size; items += one.i64NumItems; }
        }
        return (bytes, items);
    }

    public static void EmptyRecycleBin() =>
        Win32.SHEmptyRecycleBinW(IntPtr.Zero, null, Win32.SHERB_NOCONFIRMATION | Win32.SHERB_NOPROGRESSUI | Win32.SHERB_NOSOUND);

    /// <summary>Moves a file or folder to the Recycle Bin, so the user can still restore it.</summary>
    public static bool Recycle(string path)
    {
        var op = new Win32.SHFILEOPSTRUCT
        {
            wFunc = Win32.FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = (ushort)(Win32.FOF_ALLOWUNDO | Win32.FOF_NOCONFIRMATION | Win32.FOF_SILENT | Win32.FOF_NOERRORUI),
        };
        return Win32.SHFileOperationW(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>Resolves "@{package?ms-resource://...}" and "@dll,-123" style strings.</summary>
    public static string? IndirectString(string? s)
    {
        if (string.IsNullOrEmpty(s) || s[0] != '@') return s;
        var sb = new StringBuilder(1024);
        return Win32.SHLoadIndirectString(s, sb, (uint)sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
    }

    /// <summary>
    /// Resolves a Store package's display name. Packages store either a ready string, an "@{...}" reference,
    /// or a bare "ms-resource:Key" that has to be qualified with the package before Windows can load it.
    /// </summary>
    public static string? PackageString(string packageFullName, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw[0] == '@') return IndirectString(raw);
        if (!raw.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return raw;
        string key = raw["ms-resource:".Length..];
        string name = packageFullName.Split('_')[0];
        string[] candidates = key.StartsWith("//", StringComparison.Ordinal) ? [$"@{{{packageFullName}?ms-resource:{key}}}"]
            : key.Contains('/') ? [$"@{{{packageFullName}?ms-resource://{name}/{key.TrimStart('/')}}}"]
            : [$"@{{{packageFullName}?ms-resource://{name}/Resources/{key}}}", $"@{{{packageFullName}?ms-resource://{name}/{key}}}"];
        foreach (var candidate in candidates)
        {
            var text = IndirectString(candidate);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    /// <summary>"Microsoft.YourPhone" → "YourPhone": a readable fallback when a package has no resolvable display name.</summary>
    public static string PrettyPackageName(string packageName)
    {
        string last = packageName.Split('_')[0].Split('.')[^1];
        return last.Length > 2 ? last : packageName.Split('_')[0];
    }

    /// <summary>True while a full-screen game, video or presentation owns the screen.</summary>
    public static bool IsFullscreenBusy() => Win32.SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4;
}

public static class Paths
{
    public static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    public static string SystemDrive => Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
    public static string LocalLow => Path.Combine(Profile, "AppData", "LocalLow");

    public static string Expand(string path) => Environment.ExpandEnvironmentVariables(path);

    /// <summary>Extracts the executable path from a command line such as: "C:\x\app.exe" --flag</summary>
    public static string? ExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = Expand(command.Trim());
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        // Unquoted: grow the candidate until it names an existing file ("C:\Program Files\x.exe /arg").
        int idx = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var candidate = c[..(idx + 4)];
            if (File.Exists(candidate)) return candidate;
            idx = c.IndexOf(".exe", idx + 4, StringComparison.OrdinalIgnoreCase);
        }
        int space = c.IndexOf(' ');
        return space > 0 ? c[..space] : c;
    }
}
