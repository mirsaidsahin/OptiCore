using OptiCore.Engine.Modules;

namespace OptiCore.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("0.1.0", "v0.2.0", true)]
    [InlineData("0.2.0", "v0.2.0", false)]
    [InlineData("0.2.0", "0.1.9", false)]
    [InlineData("0.2.0", "v0.10.0", true)]
    [InlineData("0.2.0", "not-a-version", false)]
    public void Release_tags_are_compared_as_versions(string current, string tag, bool newer) =>
        Assert.Equal(newer, UpdateService.IsNewer(current, tag));

    [Fact]
    public void Release_json_yields_the_exe_asset_and_its_digest()
    {
        const string json = """
            { "tag_name": "v0.2.0", "html_url": "https://github.com/x/y/releases/tag/v0.2.0", "body": "notes", "published_at": "2026-11-17T10:00:00Z",
              "assets": [
                { "name": "OptiCore-Kurulum.exe", "browser_download_url": "https://e/k", "size": 1 },
                { "name": "OptiCore.exe", "browser_download_url": "https://e/o", "size": 42, "digest": "sha256:ABCD" } ] }
            """;
        var r = UpdateService.Parse(json)!;
        Assert.Equal("0.2.0", r.Version);
        Assert.Equal("https://e/o", r.Download);
        Assert.Equal(42, r.Size);
        Assert.Equal("ABCD", r.Sha256);
    }
}
