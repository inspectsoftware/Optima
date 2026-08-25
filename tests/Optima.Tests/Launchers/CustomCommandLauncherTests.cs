using Optima.Platform.Windows.Launchers;
using Xunit;

namespace Optima.Tests.Launchers;

public class CustomCommandLauncherTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\x.exe\" --flag", "C:\\Program Files\\x.exe", "--flag")]
    [InlineData("C:\\x.exe --flag value", "C:\\x.exe", "--flag value")]
    [InlineData("C:\\x.exe", "C:\\x.exe", "")]
    [InlineData("\"C:\\x.exe\"", "C:\\x.exe", "")]
    public void SplitCommand_HandlesQuotingVariants(string command, string expectedExe, string expectedArgs)
    {
        var (exe, args) = CustomCommandLauncher.SplitCommand(command);
        Assert.Equal(expectedExe, exe);
        Assert.Equal(expectedArgs, args);
    }
}
