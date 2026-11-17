using System.Security;
using System.Text.Json;
using OptiCore.Engine.Platform;

namespace OptiCore.Engine.Core;

public sealed class RpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static RpcException Invalid(string message) => new("INVALID", message);
    public static RpcException NotFound(string message) => new("NOT_FOUND", message);
    public static RpcException Unsupported(string message) => new("UNSUPPORTED", message);
    public static RpcException Denied(string message) => new("DENIED", message);
    public static RpcException Failed(string message) => new("FAILED", message);

    public static RpcException Elevation() => new("ELEVATION_REQUIRED",
        Loc.S("Bu işlem yönetici yetkisi gerektirir. OptiCore'u yönetici olarak yeniden başlatın.",
              "This action needs administrator rights. Restart OptiCore as administrator."));
}

/// <summary>Typed access to the JSON params of a call.</summary>
public sealed class RpcContext(JsonElement p)
{
    public JsonElement P { get; } = p;

    private bool Try(string name, out JsonElement e)
    {
        if (P.ValueKind == JsonValueKind.Object && P.TryGetProperty(name, out e)
            && e.ValueKind != JsonValueKind.Null && e.ValueKind != JsonValueKind.Undefined) return true;
        e = default;
        return false;
    }

    public bool Has(string name) => Try(name, out _);

    public string Str(string name) =>
        Try(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()!
        : throw RpcException.Invalid($"Missing parameter '{name}'");

    public string Str(string name, string fallback) =>
        Try(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : fallback;

    public string? StrOrNull(string name) =>
        Try(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    public double Dbl(string name, double fallback = 0) =>
        Try(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : fallback;

    public int Int(string name, int fallback = 0) => (int)Math.Round(Dbl(name, fallback));
    public long Long(string name, long fallback = 0) => (long)Math.Round(Dbl(name, fallback));

    public bool Bool(string name, bool fallback = false) =>
        Try(name, out var e) ? e.ValueKind == JsonValueKind.True || (e.ValueKind != JsonValueKind.False && fallback) : fallback;

    public string[] StrArr(string name)
    {
        if (!Try(name, out var e) || e.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var x in e.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString()!);
        return [.. list];
    }

    public int[] IntArr(string name)
    {
        if (!Try(name, out var e) || e.ValueKind != JsonValueKind.Array) return [];
        var list = new List<int>();
        foreach (var x in e.EnumerateArray()) if (x.ValueKind == JsonValueKind.Number) list.Add((int)x.GetDouble());
        return [.. list];
    }

    public T Get<T>(string name) =>
        Try(name, out var e) ? e.Deserialize<T>(Json.Rpc) ?? throw RpcException.Invalid($"Bad parameter '{name}'")
        : throw RpcException.Invalid($"Missing parameter '{name}'");

    public T? GetOrDefault<T>(string name) => Try(name, out var e) ? e.Deserialize<T>(Json.Rpc) : default;
}

/// <summary>
/// Transport-agnostic method router. The desktop shell feeds it WebView2 messages; the dev
/// server feeds it HTTP bodies. Request: {id, method, params, lang}. Response: {id, ok, result|error}.
/// </summary>
public sealed class RpcRouter
{
    private readonly Dictionary<string, Func<RpcContext, Task<object?>>> _handlers = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Methods => _handlers.Keys;

    /// <summary>Synchronous handler that returns a value.</summary>
    public void On(string method, Func<RpcContext, object?> handler) =>
        _handlers[method] = c => Task.FromResult(handler(c));

    /// <summary>Synchronous handler with no result; the caller receives true.</summary>
    public void Do(string method, Action<RpcContext> handler) =>
        _handlers[method] = c => { handler(c); return Task.FromResult<object?>(true); };

    public void OnAsync(string method, Func<RpcContext, Task<object?>> handler) => _handlers[method] = handler;

    public async Task<string> HandleAsync(string requestJson)
    {
        long id = 0;
        string method = "?";
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number) id = idEl.GetInt64();
            method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
            Loc.Current = root.TryGetProperty("lang", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : Loc.Default;
            JsonElement p = root.TryGetProperty("params", out var pe) ? pe.Clone() : default;

            if (!_handlers.TryGetValue(method, out var handler))
                return Error(id, "NOT_FOUND", $"Unknown method '{method}'");

            // Always leave the caller's thread (the UI thread in the desktop shell).
            object? result = await Task.Run(() => handler(new RpcContext(p))).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { id, ok = true, result }, Json.Rpc);
        }
        catch (RpcException e)
        {
            return Error(id, e.Code, e.Message);
        }
        catch (OperationCanceledException)
        {
            return Error(id, "CANCELLED", Loc.S("İşlem iptal edildi.", "The operation was cancelled."));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException)
        {
            return Elevation.IsElevated
                ? Error(id, "DENIED", Loc.S("Windows bu işleme izin vermedi: ", "Windows denied this operation: ") + e.Message)
                : Error(id, "ELEVATION_REQUIRED", RpcException.Elevation().Message);
        }
        catch (Exception e)
        {
            Log.Error($"rpc {method} failed", e);
            return Error(id, "FAILED", e.Message);
        }
    }

    private static string Error(long id, string code, string message) =>
        JsonSerializer.Serialize(new { id, ok = false, error = new { code, message } }, Json.Rpc);
}
