using OptiCore.Engine.Modules;

namespace OptiCore.Tests;

public class OverclockTests
{
    [Theory]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700K", false, "unlocked")]
    [InlineData("Intel(R) Core(TM) i5-12600KF", false, "unlocked")]
    [InlineData("12th Gen Intel(R) Core(TM) i5-1235U", true, "locked")]
    [InlineData("Intel(R) Core(TM) i7-12700", false, "locked")]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13980HX", true, "limited")]
    [InlineData("Intel(R) Core(TM) Ultra 7 265K", false, "unlocked")]
    [InlineData("AMD Ryzen 7 7700X 8-Core Processor", false, "unlocked")]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", false, "limited")]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", true, "locked")]
    [InlineData("AMD Ryzen 5 5500U with Radeon Graphics", true, "locked")]
    public void Cpu_unlock_status_is_read_from_the_model(string name, bool laptop, string expected) =>
        Assert.Equal(expected, OverclockService.ClassifyCpu(name, laptop).Status);

    [Fact]
    public void Memory_below_rated_speed_is_detected()
    {
        var mem = new MemInfo { Modules = [new MemModule { SpeedMhz = 3600, ConfiguredMhz = 2133 }, new MemModule { SpeedMhz = 3600, ConfiguredMhz = 2133 }] };
        Assert.True(OverclockService.MemorySpeed(mem).Below);
        mem.Modules.ForEach(m => m.ConfiguredMhz = 3600);
        Assert.False(OverclockService.MemorySpeed(mem).Below);
    }
}
