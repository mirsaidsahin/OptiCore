using Microsoft.Win32;
using OptiCore.Engine.Core;
using OptiCore.Engine.Journal;
using OptiCore.Engine.Platform;

namespace OptiCore.Tests;

/// <summary>
/// The undo guarantee, tested against the real registry. Every path is redirected into a throw-away key
/// under HKCU\Software\OptiCoreTests, which is deleted afterwards.
/// </summary>
[Collection("registry")]
public sealed class JournalTests : IDisposable
{
    private readonly string _sandbox = @"HKCU\Software\OptiCoreTests\" + Guid.NewGuid().ToString("N");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oc-journal-" + Guid.NewGuid().ToString("N"));
    private const string Key = @"HKCU\Software\Example\Tweak";

    public JournalTests() => Reg.SandboxRoot = _sandbox;

    public void Dispose()
    {
        Reg.SandboxRoot = null;
        using (var root = Registry.CurrentUser.OpenSubKey(@"Software\OptiCoreTests", true))
        {
            root?.DeleteSubKeyTree(_sandbox[(_sandbox.LastIndexOf('\\') + 1)..], false);
            if (root is not null && root.SubKeyCount == 0) Registry.CurrentUser.DeleteSubKey(@"Software\OptiCoreTests", false);
        }
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private ChangeJournal Journal(bool dryRun = false) => new(new DataStore(_dir), dryRun);

    [Fact]
    public void Setting_a_new_value_and_reverting_removes_it()
    {
        var journal = Journal();
        ChangeSet set;
        using (var scope = journal.Begin("test", "t")) { scope.RegSet(Key, "Enabled", RegistryValueKind.DWord, 0); set = scope.Commit(); }
        Assert.Equal(0, Reg.GetInt(Key, "Enabled"));
        var (restored, failed) = journal.Revert(set.Id);
        Assert.Equal((1, 0), (restored, failed));
        Assert.Null(Reg.Get(Key, "Enabled"));
    }

    [Fact]
    public void Overwriting_a_value_and_reverting_restores_the_exact_previous_value_and_kind()
    {
        using (var k = Reg.Create(Key)) k.SetValue("Mode", "original", RegistryValueKind.ExpandString);
        var journal = Journal();
        ChangeSet set;
        using (var scope = journal.Begin("test", "t")) { scope.RegSet(Key, "Mode", RegistryValueKind.DWord, 5); set = scope.Commit(); }
        Assert.Equal(5, Reg.GetInt(Key, "Mode"));
        journal.Revert(set.Id);
        using var key = Reg.Open(Key)!;
        Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("Mode"));
        Assert.Equal("original", key.GetValue("Mode", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }

    [Fact]
    public void Deleting_a_key_tree_and_reverting_recreates_it()
    {
        using (var k = Reg.Create(Key + @"\Child")) { k.SetValue("Bin", new byte[] { 1, 2, 3 }); k.SetValue("Multi", new[] { "a", "b" }); }
        using (var k = Reg.Create(Key)) k.SetValue("Q", 1L << 40, RegistryValueKind.QWord);
        var journal = Journal();
        ChangeSet set;
        using (var scope = journal.Begin("test", "t")) { scope.RegDeleteKey(Key); set = scope.Commit(); }
        Assert.False(Reg.Exists(Key));
        journal.Revert(set.Id);
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])Reg.Get(Key + @"\Child", "Bin")!);
        Assert.Equal(new[] { "a", "b" }, (string[])Reg.Get(Key + @"\Child", "Multi")!);
        Assert.Equal(1L << 40, (long)Reg.Get(Key, "Q")!);
    }

    [Fact]
    public void Changes_are_undone_newest_first()
    {
        var journal = Journal();
        ChangeSet set;
        using (var scope = journal.Begin("test", "t"))
        {
            scope.RegSet(Key, "V", RegistryValueKind.DWord, 1);
            scope.RegSet(Key, "V", RegistryValueKind.DWord, 2);
            set = scope.Commit();
        }
        journal.Revert(set.Id);
        Assert.Null(Reg.Get(Key, "V")); // back to "absent", not to the intermediate 1
    }

    [Fact]
    public void Simulation_records_but_does_not_touch_the_system()
    {
        var journal = Journal(dryRun: true);
        ChangeSet set;
        using (var scope = journal.Begin("test", "t")) { scope.RegSet(Key, "X", RegistryValueKind.DWord, 1); set = scope.Commit(); }
        Assert.Null(Reg.Get(Key, "X"));
        Assert.True(set.Simulated);
        Assert.False(set.CanRevert);
        Assert.Single(set.Entries);
    }

    [Fact]
    public void Journal_survives_a_restart()
    {
        var journal = Journal();
        string id;
        using (var scope = journal.Begin("test", Loc.T("Deneme", "Test"))) { scope.RegSet(Key, "P", RegistryValueKind.String, "x"); id = scope.Commit().Id; }
        var reopened = Journal();
        var set = reopened.Get(id)!;
        Assert.Equal("Test", set.Title.En);
        reopened.Revert(id);
        Assert.Null(Reg.Get(Key, "P"));
    }

    [Theory]
    [InlineData(RegistryValueKind.DWord, -1)]
    [InlineData(RegistryValueKind.String, "metin")]
    public void Encode_decode_round_trips(RegistryValueKind kind, object value) =>
        Assert.Equal(value, RegState.Decode(RegState.Encode(value), kind));
}
