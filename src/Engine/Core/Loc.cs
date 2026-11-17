using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OptiCore.Engine.Core;

/// <summary>
/// Request-scoped UI language. It is an AsyncLocal, so it flows into background jobs
/// started from an RPC call, and never leaks between concurrent requests.
/// </summary>
public static class Loc
{
    private static readonly AsyncLocal<string?> Lang = new();
    public static string Default { get; set; } = "tr";

    public static string Current
    {
        get => Lang.Value ?? Default;
        set => Lang.Value = value == "en" ? "en" : "tr";
    }

    public static bool IsEn => Current == "en";
    public static LText T(string tr, string en) => new(tr, en);
    public static string S(string tr, string en) => IsEn ? en : tr;
}

/// <summary>
/// A bilingual text. Over RPC it serializes as a plain string in the caller's language;
/// on disk it keeps both languages, so stored records can be shown in either one later.
/// </summary>
public readonly record struct LText(string Tr, string En)
{
    private static readonly CultureInfo TrCulture = CultureInfo.GetCultureInfo("tr-TR");
    public static readonly LText Empty = new("", "");

    public override string ToString() => Loc.IsEn ? En : Tr;
    public static implicit operator LText(string s) => new(s, s);

    /// <summary>Formats both variants; numbers follow each language's conventions.</summary>
    public LText F(params object?[] args) =>
        new(string.Format(TrCulture, Tr, args), string.Format(CultureInfo.InvariantCulture, En, args));
}

/// <summary>Byte count that prints as "12,4 GB" / "12.4 GB" depending on the format provider.</summary>
public readonly record struct ByteSize(long Bytes) : IFormattable
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public string ToString(string? format, IFormatProvider? provider)
    {
        double v = Math.Abs((double)Bytes);
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        string num = u == 0 ? v.ToString("0", provider) : v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", provider);
        return (Bytes < 0 ? "-" : "") + num + " " + Units[u];
    }

    public override string ToString() => ToString(null, CultureInfo.InvariantCulture);
}

public sealed class LTextRpcConverter : JsonConverter<LText>
{
    public override LText Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => LTextJson.Read(ref reader);
    public override void Write(Utf8JsonWriter writer, LText value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

public sealed class LTextStoreConverter : JsonConverter<LText>
{
    public override LText Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => LTextJson.Read(ref reader);

    public override void Write(Utf8JsonWriter writer, LText value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("tr", value.Tr);
        writer.WriteString("en", value.En);
        writer.WriteEndObject();
    }
}

internal static class LTextJson
{
    public static LText Read(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString() ?? "";
            return new LText(s, s);
        }
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return LText.Empty;
        string tr = root.TryGetProperty("tr", out var t) ? t.GetString() ?? "" : "";
        string en = root.TryGetProperty("en", out var e) ? e.GetString() ?? "" : tr;
        return new LText(tr, en);
    }
}

/// <summary>Writes finite doubles rounded to 3 decimals; NaN/Infinity become 0 so JSON stays valid.</summary>
public sealed class SafeDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDouble();
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(double.IsFinite(value) ? Math.Round(value, 3) : 0);
}

public sealed class SafeFloatConverter : JsonConverter<float>
{
    public override float Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetSingle();
    public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(float.IsFinite(value) ? MathF.Round(value, 3) : 0);
}

public static class Json
{
    /// <summary>Wire format: LText collapses to the current language.</summary>
    public static readonly JsonSerializerOptions Rpc = Make(new LTextRpcConverter(), indented: false);
    /// <summary>On-disk format: indented, LText keeps both languages.</summary>
    public static readonly JsonSerializerOptions Store = Make(new LTextStoreConverter(), indented: true);
    /// <summary>On-disk JSON-lines format.</summary>
    public static readonly JsonSerializerOptions StoreLine = Make(new LTextStoreConverter(), indented: false);

    private static JsonSerializerOptions Make(JsonConverter ltext, bool indented)
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            IncludeFields = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(ltext);
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        o.Converters.Add(new SafeDoubleConverter());
        o.Converters.Add(new SafeFloatConverter());
        return o;
    }

    public static string ToRpc(object? value) => JsonSerializer.Serialize(value, Rpc);
}
