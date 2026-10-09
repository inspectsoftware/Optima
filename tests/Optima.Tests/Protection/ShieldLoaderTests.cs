using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Ipc;
using Optima.Core.Protection;
using Optima.Tests.Launch;
using Xunit;

namespace Optima.Tests.Protection;

/// <summary>
/// The loader is all the public app knows about protected play: where the module is, when to start
/// it, and how to do that without ever putting an administrator prompt in front of someone who did
/// not just press PLAY.
/// </summary>
public sealed class ShieldLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optima-shield-loader", Guid.NewGuid().ToString("N"));
    private readonly FakeElevationBroker _helper = new();
    private readonly List<string> _started = [];
    private readonly List<ShieldLoader> _loaders = [];
    private bool _administrator = true;
    private DateTimeOffset _now = new(2026, 10, 8, 18, 0, 0, TimeSpan.Zero);

    private string AppDirectory => Path.Combine(_root, "app");
    private string DataDirectory => Path.Combine(_root, "data");

    public ShieldLoaderTests()
    {
        Directory.CreateDirectory(AppDirectory);
        Directory.CreateDirectory(DataDirectory);
    }

    public void Dispose()
    {
        _loaders.ForEach(loader => loader.Dispose());
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private ShieldLoader Loader(bool installed = true, bool linked = true, bool bundled = false)
    {
        if (installed)
        {
            File.WriteAllText(Path.Combine(AppDirectory, ShieldLoader.ExeName), "module");
        }
        var loader = new ShieldLoader(_helper, AppDirectory, DataDirectory,
            (exe, arguments, wait) =>
            {
                _started.Add(Path.GetFileName(exe) + " " + arguments);
                return arguments == "--print-public-key" ? "PUBLICKEY" : string.Empty;
            },
            () => _administrator, () => _now, NullLogger<ShieldLoader>.Instance, bundled);
        _loaders.Add(loader);
        if (linked)
        {
            loader.WriteLink(246001782, null);
        }
        return loader;
    }

    private void Status(string state, bool elevated = false, string message = "", int secondsOld = 0)
        => File.WriteAllText(Path.Combine(DataDirectory, "shield-status.json"),
            $$"""{"state":"{{state}}","message":"{{message}}","elevated":{{(elevated ? "true" : "false")}},"mode":"play","pid":1,"updatedUnix":{{_now.ToUnixTimeSeconds() - secondsOld}}}""");

    private IEnumerable<string> Launches => _helper.Sent.Where(r => r.Command == IpcCommand.LaunchShield).Select(r => r.Args["mode"]);

    [Fact]
    public async Task ABuildWithoutTheModuleStartsNothingAndSaysSo()
    {
        var loader = Loader(installed: false);

        await loader.StartAsync("play", allowPrompt: true);

        Assert.Empty(_started);
        Assert.Equal(0, _helper.Prompts);
        Assert.Equal(ShieldPresence.NoModule, loader.Read().Kind);
    }

    [Fact]
    public async Task AnOfficialBuildThatLostTheModuleSaysItWasRemoved()
    {
        var loader = Loader(installed: false, bundled: true);

        await loader.StartAsync("play", allowPrompt: true);

        Assert.Empty(_started);
        Assert.Equal(ShieldPresence.Removed, loader.Read().Kind);
    }

    // The disclosure gate: the module does not start on a PC whose player has not read the current text.

    [Fact]
    public async Task ALinkFromBeforeTheCurrentTextStartsNothingUntilItWasRead()
    {
        _helper.IsConnected = true;
        var loader = Loader(linked: false);
        File.WriteAllText(Path.Combine(DataDirectory, "shield-link.json"),
            $$"""{"accountId":246001782,"disclosureVersion":{{ShieldLoader.DisclosureVersion - 1}},"botUrl":"http://127.0.0.1:5099"}""");

        Assert.True(loader.NeedsDisclosure);
        await loader.StartAsync("play", allowPrompt: true);

        Assert.Empty(_started);
        Assert.Empty(_helper.Sent);
        Assert.Equal(0, _helper.Prompts);
        Assert.Equal(ShieldPresence.NeedsDisclosure, loader.Read().Kind);

        loader.AcceptDisclosure();

        Assert.False(loader.NeedsDisclosure);
        await loader.StartAsync("play", allowPrompt: true);
        Assert.Equal(["play"], Launches);
        // Reading it changed nothing else about the link.
        var json = File.ReadAllText(Path.Combine(DataDirectory, "shield-link.json"));
        Assert.Contains("\"accountId\":246001782", json);
        Assert.Contains("\"botUrl\":\"http://127.0.0.1:5099\"", json);
    }

    [Fact]
    public void APcThatIsNotLinkedHasNothingToRead()
    {
        var loader = Loader(linked: false);

        Assert.False(loader.NeedsDisclosure);
        loader.AcceptDisclosure();

        Assert.False(loader.Linked);
    }

    [Fact]
    public async Task APcThatIsNotLinkedIsNeverPromptedAndNothingStarts()
    {
        var loader = Loader(linked: false);

        await loader.StartAsync("play", allowPrompt: true);

        Assert.Empty(_started);
        Assert.Equal(0, _helper.Prompts);
        Assert.Equal(ShieldPresence.NotLinked, loader.Read().Kind);
    }

    [Fact]
    public async Task WithTheHelperUpTheModuleIsStartedThroughItWithOnlyAMode()
    {
        _helper.IsConnected = true;

        await Loader().StartAsync("watch", allowPrompt: false);

        Assert.Equal(["watch"], Launches);
        Assert.Equal(["mode"], _helper.Sent.Single().Args.Keys);
        Assert.Empty(_started);
        Assert.Equal(0, _helper.Prompts);
    }

    [Fact]
    public async Task ABackgroundStartNeverPromptsAndRunsTheModuleWithoutAdministratorRights()
    {
        await Loader().StartAsync("watch", allowPrompt: false);

        Assert.Equal(0, _helper.Prompts);
        Assert.Equal(["Optima.Shield.exe mode=watch"], _started);
    }

    [Fact]
    public async Task AnAccountThatIsNotAnAdministratorIsNeverPrompted()
    {
        _administrator = false;

        await Loader().StartAsync("play", allowPrompt: true);

        Assert.Equal(0, _helper.Prompts);
        Assert.Equal(["Optima.Shield.exe mode=play"], _started);
    }

    [Fact]
    public async Task PlaySharesTheOnePromptAndAnAcceptedOneStartsTheModuleElevated()
    {
        _helper.ConnectOnPrompt = true;

        await Loader().StartAsync("play", allowPrompt: true);

        Assert.Equal(1, _helper.Prompts);
        Assert.Equal(["play"], Launches);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task ADeclinedPromptIsNotAskedAgainAndTheModuleRunsWithoutTheRights()
    {
        _helper.LastStartFailure = ElevationStartFailure.Declined;
        var loader = Loader();

        await loader.StartAsync("play", allowPrompt: true);
        await loader.StartAsync("play", allowPrompt: true);

        Assert.Equal(1, _helper.Prompts);
        Assert.Equal(["Optima.Shield.exe mode=play", "Optima.Shield.exe mode=play"], _started);
    }

    [Fact]
    public async Task AHelperThatRefusesTheModuleStillLeavesItRunningWithoutTheRights()
    {
        _helper.IsConnected = true;
        _helper.Refuse = true;

        await Loader().StartAsync("play", allowPrompt: true);

        Assert.Equal(["Optima.Shield.exe mode=play"], _started);
    }

    [Fact]
    public async Task AModuleAlreadyRunningElevatedIsLeftAlone()
    {
        var loader = Loader();
        Status("protected", elevated: true);

        await loader.StartAsync("play", allowPrompt: true);

        Assert.Equal(0, _helper.Prompts);
        Assert.Empty(_started);
        Assert.Empty(_helper.Sent);
    }

    [Fact]
    public void TheStatusFileIsReadForWhatItSaysAndForHowOldItIs()
    {
        var loader = Loader();
        Assert.Equal(ShieldPresence.Stopped, loader.Read().Kind);

        Status("protected", elevated: true);
        Assert.Equal(new ShieldState(ShieldPresence.Protected, true, string.Empty), loader.Read());

        // A module that died says nothing more: its last word goes stale.
        Status("protected", secondsOld: 46);
        Assert.Equal(ShieldPresence.Stopped, loader.Read().Kind);

        Status("unprotected", message: "This account is not linked.", secondsOld: 600);
        Assert.Equal(new ShieldState(ShieldPresence.Unprotected, false, "This account is not linked."), loader.Read());

        Status("stopped");
        Assert.Equal(ShieldPresence.Stopped, loader.Read().Kind);

        File.WriteAllText(Path.Combine(DataDirectory, "shield-status.json"), "{ not json");
        Assert.Equal(ShieldPresence.Stopped, loader.Read().Kind);
    }

    [Fact]
    public async Task AModuleThatWentSilentDuringASessionIsStartedAgainOnce()
    {
        var loader = Loader();
        await loader.StartAsync("play", allowPrompt: false);
        Status("protected", secondsOld: 120);
        _now = _now.AddSeconds(60);

        await loader.CheckAsync();
        await loader.CheckAsync();

        Assert.Equal(2, _started.Count);

        // The next session gets its own one.
        await loader.StartAsync("play", allowPrompt: false);
        _now = _now.AddSeconds(60);
        await loader.CheckAsync();
        Assert.Equal(4, _started.Count);
    }

    [Fact]
    public async Task AModuleThatStaysGoneAfterTheSecondStartIsSaidOnce()
    {
        var loader = Loader();
        var told = new List<ShieldState>();
        loader.Changed += told.Add;
        await loader.StartAsync("play", allowPrompt: false);

        for (var check = 0; check < 5; check++)
        {
            _now = _now.AddSeconds(30);
            await loader.CheckAsync();
        }

        Assert.Equal(2, _started.Count);
        Assert.Equal(new ShieldState(ShieldPresence.Unprotected, false, ShieldLoader.NotRunning), Assert.Single(told));
    }

    [Fact]
    public async Task AReasonLeftBehindByAnEarlierRunIsNotThisSessions()
    {
        var loader = Loader();
        var told = new List<ShieldState>();
        loader.Changed += told.Add;
        Status("unprotected", message: "OptimaBot did not answer.", secondsOld: 600);

        await loader.StartAsync("play", allowPrompt: false);
        _now = _now.AddSeconds(30);
        await loader.CheckAsync();

        // Read as "not running", which starts it once more, and the old sentence is not shown.
        Assert.Equal(2, _started.Count);
        Assert.Empty(told);
    }

    [Fact]
    public void AStatusFromAfterNowIsAsStaleAsAnOldOne()
    {
        var loader = Loader();
        Status("protected", secondsOld: -3600);

        Assert.Equal(ShieldPresence.Stopped, loader.Read().Kind);
    }

    [Fact]
    public async Task AModuleTheBotRefusedIsNotStartedAgainAndThePlayerIsToldOnce()
    {
        var loader = Loader();
        var told = new List<ShieldState>();
        loader.Changed += told.Add;
        await loader.StartAsync("play", allowPrompt: false);
        Status("unprotected", message: "This Optima build is not recognised.");
        _now = _now.AddSeconds(60);

        await loader.CheckAsync();
        await loader.CheckAsync();

        Assert.Single(_started);
        Assert.Equal("This Optima build is not recognised.", Assert.Single(told).Message);
    }

    [Fact]
    public async Task AfterTheSessionNothingIsRestarted()
    {
        var loader = Loader();
        await loader.StartAsync("play", allowPrompt: false);
        loader.SessionEnded();
        _now = _now.AddSeconds(120);

        await loader.CheckAsync();

        Assert.Single(_started);
    }

    [Fact]
    public void LinkingWritesWhatTheModuleReadsAndUnlinkingRemovesIt()
    {
        var loader = Loader(linked: false);
        Assert.False(loader.Linked);

        loader.WriteLink(246001782, "http://127.0.0.1:5099");

        Assert.True(loader.Linked);
        var json = File.ReadAllText(Path.Combine(DataDirectory, "shield-link.json"));
        Assert.Contains("\"accountId\":246001782", json);
        Assert.Contains("\"disclosureVersion\":" + ShieldLoader.DisclosureVersion, json);
        Assert.Contains("\"botUrl\":\"http://127.0.0.1:5099\"", json);

        loader.RemoveLink();
        Assert.False(loader.Linked);
    }

    [Fact]
    public void TheKeyAndTheStopAreAskedOfTheModuleItself()
    {
        var loader = Loader();

        Assert.Equal("PUBLICKEY", loader.PublicKey());
        loader.Stop();

        Assert.Equal(["Optima.Shield.exe --print-public-key", "Optima.Shield.exe --stop"], _started);
    }

    [Fact]
    public void WithoutTheModuleThereIsNoKeyAndNothingToStop()
    {
        var loader = Loader(installed: false);

        Assert.Null(loader.PublicKey());
        loader.Stop();

        Assert.Empty(_started);
    }
}
