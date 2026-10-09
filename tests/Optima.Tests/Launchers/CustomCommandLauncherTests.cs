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

    [Fact]
    public void SplitCommand_FindsAnUnquotedProgramWhosePathHasSpaces()
    {
        var folder = Path.Combine(Path.GetTempPath(), "optima launcher " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var program = Path.Combine(folder, "my tool.exe");
        File.WriteAllText(program, string.Empty);
        try
        {
            var (exe, args) = CustomCommandLauncher.SplitCommand(program + " --uri x y");
            Assert.Equal(program, exe);
            Assert.Equal("--uri x y", args);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
