using OptiCore.Engine.Modules;

namespace OptiCore.Tests;

public class GameKnowledgeTests
{
    [Theory]
    [InlineData("steam:227300", @"C:\Steam\steamapps\common\Euro Truck Simulator 2", "ets2")]
    [InlineData("steam:730", @"C:\Steam\steamapps\common\Counter-Strike Global Offensive", "cs2")]
    [InlineData("epic:Fortnite", @"C:\Epic\Fortnite", "fortnite")]
    [InlineData("roblox:player", @"C:\Users\x\AppData\Local\Roblox\Versions", "roblox")]
    // Same game from another store: recognized by its folder name.
    [InlineData("xbox:ForzaHorizon5", @"C:\XboxGames\Forza Horizon 5\Content", "forza5")]
    [InlineData("custom:abc", @"D:\Games\Cyberpunk 2077", "cyberpunk")]
    public void Games_are_recognized_by_store_id_or_folder(string id, string folder, string key) =>
        Assert.Equal(key, GameKnowledge.Identify(new Game { Id = id, Folder = folder })?.Key);

    [Fact]
    public void Unknown_games_get_no_notes() =>
        Assert.Null(GameKnowledge.Identify(new Game { Id = "steam:1", Folder = @"C:\Steam\steamapps\common\Some Indie Game" }));

    [Fact]
    public void Every_entry_has_text_in_both_languages()
    {
        foreach (var id in new[] { "steam:227300", "steam:730", "steam:1091500", "roblox:player", "riot:valorant", "steam:1245620" })
        {
            var k = GameKnowledge.Identify(new Game { Id = id, Folder = @"C:\x" })!;
            Assert.False(string.IsNullOrWhiteSpace(k.Note.Tr));
            Assert.False(string.IsNullOrWhiteSpace(k.Note.En));
            Assert.All(k.Tips, tip => Assert.False(string.IsNullOrWhiteSpace(tip.Tr) || string.IsNullOrWhiteSpace(tip.En)));
        }
    }
}
