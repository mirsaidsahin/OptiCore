using OptiCore.Engine.Modules;

namespace OptiCore.Tests;

public class HardwareRankTests
{
    [Theory]
    [InlineData("12th Gen Intel(R) Core(TM) i5-1235U", 8000)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", 18000)]
    [InlineData("Intel(R) Core(TM) i5-13600K", 24000)]
    public void Cpu_names_from_windows_match_the_table(string name, int multi) => Assert.Equal(multi, HardwareRank.Cpu(name)?.Multi);

    [Theory]
    [InlineData("Intel(R) Iris(R) Xe Graphics", 18)]
    [InlineData("NVIDIA GeForce RTX 4070 Ti SUPER", 225)]
    [InlineData("NVIDIA GeForce RTX 4070", 175)]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", 95)]
    [InlineData("AMD Radeon RX 7600 XT", 110)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", 105)]
    public void Gpu_names_from_windows_match_the_table(string name, int index) => Assert.Equal(index, HardwareRank.Gpu(name));

    [Fact]
    public void Unknown_models_are_not_guessed()
    {
        Assert.Null(HardwareRank.Gpu("Intel(R) Arc(TM) B570 Graphics"));
        Assert.Null(HardwareRank.Cpu("Some Future CPU 9000"));
    }

    [Fact]
    public void Reference_laptop_scores_about_1000() => Assert.Equal(1000, HardwareRank.CpuScore(8000, 1600), 0);
}
