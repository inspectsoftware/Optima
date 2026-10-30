using Optima.Core.Configuration;
using Optima.Core.Detection;
using Xunit;
using static Optima.Core.Detection.VddRootNodes;

namespace Optima.Tests.Driver;

public class VddRootNodesTests
{
    [Fact]
    public void One_working_or_disabled_node_with_a_driver_is_kept()
    {
        Assert.True(CanKeep([new Node("a", 0, true)]));
        Assert.True(CanKeep([new Node("a", ProblemDisabled, true)]));
    }

    [Fact]
    public void Driverless_faulty_or_duplicate_nodes_are_rebuilt()
    {
        Assert.False(CanKeep([]));
        Assert.False(CanKeep([new Node("a", 28, false)]));
        Assert.False(CanKeep([new Node("a", 0, false)]));
        Assert.False(CanKeep([new Node("a", 43, true)]));
        Assert.False(CanKeep([new Node("a", -1, true)]));
        Assert.False(CanKeep([new Node("a", 0, true), new Node("b", ProblemDisabled, true)]));
    }

    [Fact]
    public void A_fresh_node_must_have_a_driver_and_no_fault()
    {
        Assert.Null(CheckFresh([new Node("a", 0, true)]));
        Assert.Contains("28", CheckFresh([new Node("a", 28, false)]));
        Assert.Contains("52", CheckFresh([new Node("a", 52, true)]));
        Assert.NotNull(CheckFresh([]));
        Assert.NotNull(CheckFresh([new Node("a", 0, true), new Node("b", 0, true)]));
    }

    [Fact]
    public void A_requested_rebuild_never_keeps_a_node()
    {
        // Code 22 can hide a fault Optima's own restore disabled again, so the user's reinstall rebuilds.
        Assert.False(CanKeep([new Node("a", ProblemDisabled, true)], rebuild: true));
        Assert.False(CanKeep([new Node("a", 0, true)], rebuild: true));
    }

    [Fact]
    public void A_node_waiting_for_a_restart_is_kept_and_not_a_failed_install()
    {
        Assert.True(CanKeep([new Node("a", ProblemNeedRestart, true)]));
        Assert.Null(CheckFresh([new Node("a", ProblemNeedRestart, true)]));
        Assert.Null(CheckFresh([new Node("a", ProblemNeedRestart, false)]));
        Assert.True(NeedsRestart([new Node("a", ProblemNeedRestart, true)]));
        Assert.False(NeedsRestart([new Node("a", 0, true)]));
    }

    [Fact]
    public void A_fresh_node_without_a_driver_is_fine_only_while_a_restart_is_pending()
    {
        Assert.Null(CheckFresh([new Node("a", 28, false)], restartPending: true));
        Assert.NotNull(CheckFresh([new Node("a", 28, false)], restartPending: false));
        Assert.Contains("43", CheckFresh([new Node("a", 43, true)], restartPending: true));
    }

    [Fact]
    public void Settings_location_names_the_driver_file_when_the_users_path_differs()
    {
        var (path, source) = DriverSettingsLocation.Locate(@"C:\VirtualDisplayDriver\vdd_settings.xml", () => @"D:\VDD");
        Assert.Equal(@"C:\VirtualDisplayDriver\vdd_settings.xml", path);
        Assert.Contains(@"D:\VDD\vdd_settings.xml", source);
        Assert.Equal("your setting", DriverSettingsLocation.Locate(@"d:\vdd\VDD_SETTINGS.xml", () => @"D:\VDD").Source);
    }

    [Fact]
    public void Settings_location_prefers_user_then_vddpath_then_default()
    {
        Assert.Equal(@"D:\mine.xml", DriverSettingsLocation.Resolve(@"D:\mine.xml", () => @"E:\vdd"));
        Assert.Equal(@"E:\vdd\vdd_settings.xml", DriverSettingsLocation.Resolve(null, () => @"E:\vdd"));
        Assert.Equal(VddSettingsDefaults.DefaultPath, DriverSettingsLocation.Resolve("", () => "  "));
        Assert.Equal(VddSettingsDefaults.DefaultPath, DriverSettingsLocation.Resolve(null, () => null));
    }

    [Fact]
    public void Settings_location_names_its_source_for_the_log()
    {
        Assert.Equal("your setting", DriverSettingsLocation.Locate(@"D:\mine.xml", () => null).Source);
        Assert.Equal("VDDPATH", DriverSettingsLocation.Locate(null, () => @"E:\vdd").Source);
        Assert.Equal("default", DriverSettingsLocation.Locate(null, () => null).Source);
    }
}
