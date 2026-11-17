using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using OptiCore.Engine.Core;
using OptiCore.Engine.Native;

namespace OptiCore.Engine.Platform;

/// <summary>Windows power plans through powrprof (no parsing of localized powercfg output).</summary>
public static class PowerPlans
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimateTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public sealed record Plan(Guid Id, string Name, bool Active, string Kind);

    public static Guid? Active()
    {
        if (Win32.PowerGetActiveScheme(IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { Win32.LocalFree(p); }
    }

    public static bool SetActive(Guid scheme) => Win32.PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;

    public static List<Plan> List()
    {
        var plans = new List<Plan>();
        var active = Active();
        for (uint i = 0; i < 64; i++)
        {
            Guid g = Guid.Empty;
            uint size = 16;
            if (Win32.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, Win32.ACCESS_SCHEME, i, ref g, ref size) != 0) break;
            plans.Add(new Plan(g, Name(g), g == active, Kind(g)));
        }
        return plans;
    }

    private static string Kind(Guid g) =>
        g == Balanced ? "balanced" : g == HighPerformance ? "high" : g == PowerSaver ? "saver" : g == UltimateTemplate ? "ultimate" : "custom";

    private static string Name(Guid g)
    {
        uint size = 0;
        Win32.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0) return g.ToString();
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return Win32.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                ? Marshal.PtrToStringUni(buffer) ?? g.ToString()
                : g.ToString();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

/// <summary>Windows services: start mode is read from the registry and changed through sc.exe.</summary>
public static class ServiceOps
{
    private const string Root = @"HKLM\SYSTEM\CurrentControlSet\Services\";

    /// <returns>auto | delayed-auto | demand | disabled | boot | system, or null when the service does not exist.</returns>
    public static string? StartMode(string name)
    {
        int? start = Reg.GetInt(Root + name, "Start");
        if (start is null) return null;
        return start switch
        {
            0 => "boot",
            1 => "system",
            2 => Reg.GetInt(Root + name, "DelayedAutostart") == 1 ? "delayed-auto" : "auto",
            3 => "demand",
            _ => "disabled",
        };
    }

    public static void SetStartMode(string name, string mode)
    {
        if (mode is not ("auto" or "delayed-auto" or "demand" or "disabled")) throw new ArgumentException("Unsupported start mode: " + mode);
        if (name.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or ' ' or '$'))) throw new ArgumentException("Invalid service name");
        var r = Proc.Run(Proc.Sys("sc.exe"), $"config \"{name}\" start= {mode}");
        if (!r.Ok) throw r.ExitCode == 5 ? new UnauthorizedAccessException("sc config") : new InvalidOperationException("sc config: " + r.Text);
    }
}

/// <summary>Task Scheduler through its COM automation API.</summary>
public static class TaskSched
{
    public static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is unavailable");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static dynamic Find(string taskPath)
    {
        dynamic service = Connect();
        int cut = taskPath.LastIndexOf('\\');
        string folder = cut <= 0 ? "\\" : taskPath[..cut];
        return service.GetFolder(folder).GetTask(taskPath[(cut + 1)..]);
    }

    public static bool? GetEnabled(string taskPath)
    {
        try { return (bool)Find(taskPath).Enabled; } catch { return null; }
    }

    public static void SetEnabled(string taskPath, bool enabled)
    {
        try { Find(taskPath).Enabled = enabled; }
        catch (COMException e) when ((uint)e.HResult == 0x80070005) { throw new UnauthorizedAccessException("Task Scheduler", e); }
    }
}

/// <summary>Registry values as strings, so they can be journaled and restored exactly.</summary>
public static class RegState
{
    public static Dictionary<string, string?> Read(string path, string name, RegistryView view)
    {
        var state = new Dictionary<string, string?> { ["exists"] = "0" };
        using var key = Reg.Open(path, false, view);
        if (key is null) return state;
        object? value;
        try { value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); } catch { return state; }
        if (value is null) return state;
        var kind = key.GetValueKind(name);
        state["exists"] = "1";
        state["kind"] = kind.ToString();
        state["data"] = Encode(value);
        return state;
    }

    public static void Write(string path, string name, Dictionary<string, string?> state, RegistryView view)
    {
        if (state.GetValueOrDefault("exists") != "1")
        {
            using var existing = Reg.Open(path, true, view);
            existing?.DeleteValue(name, throwOnMissingValue: false);
            return;
        }
        var kind = Enum.Parse<RegistryValueKind>(state["kind"] ?? "String");
        using var key = Reg.Create(path, view);
        key.SetValue(name, Decode(state.GetValueOrDefault("data") ?? "", kind), kind);
    }

    public static string Encode(object value) => value switch
    {
        byte[] b => Convert.ToHexString(b),
        string[] a => JsonSerializer.Serialize(a),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    public static object Decode(string data, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)long.Parse(data, CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => long.Parse(data, CultureInfo.InvariantCulture),
        RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(data) ?? [],
        RegistryValueKind.Binary or RegistryValueKind.None or RegistryValueKind.Unknown => Convert.FromHexString(data),
        _ => data,
    };

    /// <summary>Compares a live value with an expected one (kind-insensitive for numbers).</summary>
    public static bool Matches(string path, string name, RegistryValueKind kind, object expected, RegistryView view)
    {
        var live = Reg.Get(path, name, view);
        if (live is null) return false;
        return string.Equals(Encode(live), Encode(expected), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A registry subtree captured before deletion, so the deletion can be undone.</summary>
public sealed class RegNode
{
    public Dictionary<string, Dictionary<string, string?>> Values { get; set; } = [];
    public Dictionary<string, RegNode> Keys { get; set; } = [];

    public static RegNode? Capture(string path, RegistryView view, int depth = 0)
    {
        using var key = Reg.Open(path, false, view);
        if (key is null) return null;
        var node = new RegNode();
        foreach (var name in key.GetValueNames()) node.Values[name] = RegState.Read(path, name, view);
        if (depth < 24)
            foreach (var sub in key.GetSubKeyNames())
            {
                var child = Capture(path + "\\" + sub, view, depth + 1);
                if (child is not null) node.Keys[sub] = child;
            }
        return node;
    }

    public void Restore(string path, RegistryView view)
    {
        using (Reg.Create(path, view)) { }
        foreach (var (name, state) in Values) RegState.Write(path, name, state, view);
        foreach (var (sub, child) in Keys) child.Restore(path + "\\" + sub, view);
    }
}
