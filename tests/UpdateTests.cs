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

    private static UpdateService.Release Rel(string tag) => new(tag.TrimStart('v'), tag, "notes", "https://e/r", "https://e/o", 1, null, null);

    [Fact]
    public void Pending_release_is_offered_only_when_newer_and_not_skipped()
    {
        Assert.NotNull(UpdateService.PendingFor(Rel("v0.4.0"), "0.3.0", null));
        Assert.Null(UpdateService.PendingFor(Rel("v0.3.0"), "0.3.0", null));
        Assert.Null(UpdateService.PendingFor(null, "0.3.0", null));
        Assert.Null(UpdateService.PendingFor(Rel("v0.4.0"), "0.3.0", "0.4.0"));
        Assert.Null(UpdateService.PendingFor(Rel("v0.4.0"), "0.3.0", "v0.4.0"));
    }

    [Fact]
    public void A_newer_release_appears_again_after_an_older_one_was_skipped()
    {
        Assert.NotNull(UpdateService.PendingFor(Rel("v0.5.0"), "0.3.0", "0.4.0"));
    }

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
