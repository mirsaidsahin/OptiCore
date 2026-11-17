using OptiCore.Engine.Modules;

namespace OptiCore.Tests;

public class StartupGuardTests
{
    [Fact]
    public void Updater_versions_and_guids_do_not_make_a_new_item()
    {
        Assert.Equal(StartupService.StableId(@"task:\GoogleUpdaterTaskSystem159.0.8{1A2B3C4D-1111-2222-3333-444455556666}"),
                     StartupService.StableId(@"task:\GoogleUpdaterTaskSystem160.0.1{9F8E7D6C-1111-2222-3333-444455556666}"));
        Assert.Equal(StartupService.StableId("run:user:64:MicrosoftEdgeAutoLaunch_D37CC8469F64419C1B7DDE9AEF516B8D"),
                     StartupService.StableId("run:user:64:MicrosoftEdgeAutoLaunch_0011223344556677889900AABBCCDDEE"));
        Assert.NotEqual(StartupService.StableId("run:user:64:Steam"), StartupService.StableId("run:user:64:Discord"));
    }
}
