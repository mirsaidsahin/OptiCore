using OptiCore.Engine.Modules;

namespace OptiCore.Tests;

public class ThermalTests
{
    private const string Zone = @"\_TZ.TZ00";

    [Fact]
    public void A_zone_that_never_moves_becomes_static_after_enough_samples()
    {
        var tracker = new StaticZoneTracker();
        for (int i = 1; i < StaticZoneTracker.MinSamples; i++) Assert.False(tracker.Observe(Zone, 27.85));
        Assert.True(tracker.Observe(Zone, 27.85));
        Assert.True(tracker.Dirty);
        Assert.Equal(27.85, tracker.Known[Zone]);
    }

    [Fact]
    public void A_zone_that_moves_is_never_marked_static()
    {
        var tracker = new StaticZoneTracker();
        for (int i = 0; i < 200; i++) Assert.False(tracker.Observe(@"\_TZ.CPUZ", 40 + (i % 5) * 0.5));
        Assert.False(tracker.Dirty);
    }

    [Fact]
    public void A_remembered_zone_is_excluded_immediately_and_released_when_it_moves()
    {
        var tracker = new StaticZoneTracker(new Dictionary<string, double> { [Zone] = 27.85 });
        Assert.True(tracker.Observe(Zone, 27.9));      // within 0.2
        Assert.False(tracker.Dirty);
        Assert.True(tracker.Observe(Zone, 28.5));      // wobble below 1 °C keeps the mark
        Assert.False(tracker.Observe(Zone, 45.0));     // a real reading: released
        Assert.True(tracker.Dirty);
        Assert.DoesNotContain(Zone, tracker.Known.Keys);
    }

    [Fact]
    public void Hardware_monitor_sensors_are_picked_in_order_of_preference()
    {
        (string, double, string) S(string name, double v, string id = "/intelcpu/0/temperature/0") => (name, v, id);
        Assert.Equal(61, HardwareMonitorTemp.Pick([S("CPU Core #1", 70), S("CPU Package", 61), S("Core Max", 72)]));
        Assert.Equal(55, HardwareMonitorTemp.Pick([S("Core Average", 55), S("Core Max", 60), S("CPU Core #1", 70)]));
        Assert.Equal(70, HardwareMonitorTemp.Pick([S("CPU Core #1", 66), S("CPU Core #2", 70)]));
        Assert.Equal(48, HardwareMonitorTemp.Pick([S("Core (Tctl/Tdie)", 48, "/amdcpu/0/temperature/2")]));
        Assert.Null(HardwareMonitorTemp.Pick([S("GPU Core", 60, "/gpu-nvidia/0/temperature/0")]));
        Assert.Null(HardwareMonitorTemp.Pick([]));
    }
}
