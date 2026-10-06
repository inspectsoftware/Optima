using Optima.Core.Health;
using Xunit;

namespace Optima.Tests.Health;

public sealed class RedactorTests
{
    private static readonly RedactionIdentity Alice = new("alice", "GAMING-PC");

    [Theory]
    [InlineData("api_key=abc123 and more", "api_key=[REDACTED] and more")]
    [InlineData("Authorization: Bearer: eyJhbGciOi", "Authorization: Bearer=[REDACTED]")]
    [InlineData("password = hunter2", "password=[REDACTED]")]
    public void AnythingTokenShapedIsMasked(string text, string expected)
        => Assert.Equal(expected, Redactor.Redact(text, Alice));

    [Fact]
    public void AStackFrameLosesTheProfileItWasBuiltIn()
    {
        var frame = @"   at Optima.Core.Launch.LaunchOrchestrator.Run() in C:\Users\alice\Documents\Optima\src\Run.cs:line 12";

        Assert.Equal(
            @"   at Optima.Core.Launch.LaunchOrchestrator.Run() in C:\Users\[user]\Documents\Optima\src\Run.cs:line 12",
            Redactor.Redact(frame, Alice));
    }

    [Fact]
    public void TheUserAndMachineNamesAreMaskedWhereverTheyAppear()
    {
        var text = "Signed in as Alice on gaming-pc; settings for ALICE loaded";

        Assert.Equal("Signed in as [user] on [machine]; settings for [user] loaded", Redactor.Redact(text, Alice));
    }

    [Fact]
    public void AOneLetterUserNameIsOnlyMaskedInsideAProfilePath()
    {
        // Masking the name "n" as a word would eat that letter out of every line.
        var identity = new RedactionIdentity("n", "PC");

        var redacted = Redactor.Redact(@"nothing unusual in C:\Users\n\AppData\Local\Optima\config.json", identity);

        Assert.Equal(@"nothing unusual in C:\Users\[user]\AppData\Local\Optima\config.json", redacted);
    }

    [Fact]
    public void WithNoNameToMaskTheTextIsLeftAlone()
        => Assert.Equal("an ordinary line", Redactor.Redact("an ordinary line", new RedactionIdentity(string.Empty, string.Empty)));

    [Fact]
    public void SomeoneElsesProfilePathIsMaskedToo()
    {
        // The pattern is the path's shape, not the current user's name.
        Assert.Equal(@"D:\Users\[user]\Desktop\x.log", Redactor.Redact(@"D:\Users\bob\Desktop\x.log", Alice));
    }
}
