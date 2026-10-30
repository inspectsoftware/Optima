using Optima.Platform.Windows.Services;
using Xunit;
using static Optima.Platform.Windows.Services.PnpDeviceLocator;

namespace Optima.Tests.Driver;

public class PnpDeviceLocatorTests
{
    [Fact]
    public void A_driverless_node_is_found_by_its_hardware_id_alone()
    {
        Assert.True(Matches("", [@"ROOT\MTTVDD"], "Virtual Display Driver"));
        Assert.True(Matches("Virtual Display Driver by MTT", null, "Virtual Display Driver"));
        Assert.False(Matches("NVIDIA GeForce", [@"PCI\VEN_10DE"], "Virtual Display Driver"));
    }

    [Fact]
    public void Working_nodes_come_first_then_disabled_then_broken()
    {
        var sorted = HealthiestFirst(
        [
            new PnpDevice("a", "x", true, 28),
            new PnpDevice("b", "x", false, ProblemDisabled),
            new PnpDevice("c", "x", true, 0),
            new PnpDevice("d", "x", true, 43),
        ]);

        Assert.Equal(["c", "b", "a", "d"], sorted.Select(d => d.InstanceId));
        Assert.True(sorted[0].Healthy);
        Assert.False(sorted[1].Enabled);
        Assert.True(sorted[2].Enabled && !sorted[2].Healthy);
    }
}
