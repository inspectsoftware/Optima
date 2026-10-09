using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Optima.Core.Abstractions;
using Optima.Core.Ipc;

namespace Optima.Core.Protection;

/// <summary>What protected play is doing on this PC right now, as far as the app can tell.</summary>
public enum ShieldPresence
{
    /// <summary>This build of Optima was made without the protection module.</summary>
    NoModule,

    /// <summary>This build came with the module and the file is gone, which is what security software does to it.</summary>
    Removed,

    /// <summary>The player has not read what this version of protected play does. The module is not started until they have.</summary>
    NeedsDisclosure,

    /// <summary>This PC is not linked, so the module has nothing to report and is not started.</summary>
    NotLinked,

    /// <summary>The module is not running.</summary>
    Stopped,

    /// <summary>The module ran, or is running, and its reports are not being taken. The message says why.</summary>
    Unprotected,

    /// <summary>The module is running and OptimaBot is taking its reports.</summary>
    Protected,
}

/// <param name="Elevated">Whether the module has administrator rights, which is what full coverage needs.</param>
public sealed record ShieldState(ShieldPresence Kind, bool Elevated, string Message);

/// <summary>
/// Everything this app knows about protected play: where the module is, when to start it and what
/// it last said. The checks themselves are in Optima Shield, a separate program that is not part of
/// this repository. It signs its own reports and sends them to OptimaBot itself, so nothing here
/// can speak for it; what this class reads back from it only feeds the player's own screen.
///
/// Protected play is not a setting. A linked PC runs the module in every session, and unlinking is
/// what turns it off.
/// </summary>
public sealed class ShieldLoader : IDisposable
{
    public const string ExeName = "Optima.Shield.exe";

    /// <summary>What the player is told when the module is gone during a session and starting it again did not help.</summary>
    public const string NotRunning = "Optima Shield is not running.";

    /// <summary>The version of the "what protected play does" text this build shows before linking.</summary>
    public const int DisclosureVersion = 1;

    /// <summary>The module writes its status with every report, about every ten seconds. Three missed and it is gone.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(45);

    /// <summary>How long a freshly started module is given before its silence means anything.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IElevationBroker _elevation;
    private readonly string _exePath;
    private readonly string _linkPath;
    private readonly string _statusPath;
    private readonly Func<string, string, bool, string?> _run;
    private readonly Func<bool> _isAdministrator;
    private readonly Func<DateTimeOffset> _now;
    private readonly bool _bundled;
    private readonly ILogger<ShieldLoader> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Timer? _watch;
    private bool _declined;
    private string? _sessionMode;
    private bool _relaunched;
    private DateTimeOffset _startedAt;
    private ShieldState? _told;

    /// <param name="appDirectory">Where Optima.exe is: the module is the file beside it and no other.</param>
    /// <param name="dataDirectory">%LOCALAPPDATA%\Optima. The module reads and writes there whatever this app was told to use.</param>
    /// <param name="run">
    /// Starts a program with arguments. With wait it returns what the program wrote once it has
    /// exited; without, it returns at once. Null when it could not be started.
    /// </param>
    /// <param name="isAdministrator">Whether this Windows account can approve an administrator prompt itself.</param>
    /// <param name="bundled">Whether this build of Optima was made with the module. It tells a build without one from an install that lost it.</param>
    public ShieldLoader(
        IElevationBroker elevation, string appDirectory, string dataDirectory,
        Func<string, string, bool, string?> run, Func<bool> isAdministrator, Func<DateTimeOffset> now,
        ILogger<ShieldLoader> logger, bool bundled = false)
    {
        _bundled = bundled;
        _elevation = elevation;
        _exePath = Path.Combine(appDirectory, ExeName);
        _linkPath = Path.Combine(dataDirectory, "shield-link.json");
        _statusPath = Path.Combine(dataDirectory, "shield-status.json");
        _run = run;
        _isAdministrator = isAdministrator;
        _now = now;
        _logger = logger;
    }

    /// <summary>Raised when what <see cref="Read"/> returns has changed during a session.</summary>
    public event Action<ShieldState>? Changed;

    public bool Installed => File.Exists(_exePath);

    public bool Linked => File.Exists(_linkPath);

    /// <summary>
    /// Whether this PC is linked and its player last read an older "what protected play does" than
    /// this build shows. The module is not started until <see cref="AcceptDisclosure"/> was called.
    /// </summary>
    public bool NeedsDisclosure => ReadLink() is { } link && link.DisclosureVersion < DisclosureVersion;

    /// <summary>Records that the player has read the current text. The link itself is unchanged.</summary>
    public void AcceptDisclosure()
    {
        if (ReadLink() is { AccountId: > 0 } link)
        {
            File.WriteAllText(_linkPath, JsonSerializer.Serialize(link with { DisclosureVersion = DisclosureVersion }, Json));
        }
    }

    private LinkFile? ReadLink()
    {
        try
        {
            return JsonSerializer.Deserialize<LinkFile>(File.ReadAllText(_linkPath), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts the module for a session. With administrator rights when they can be had without
    /// asking anyone who did not just press PLAY, otherwise without them: the session then runs
    /// with reduced coverage and its record says so. Never throws.
    /// </summary>
    /// <param name="mode">"play" for a launch from Optima, "watch" for a game Optima found running.</param>
    /// <param name="allowPrompt">True only for PLAY, where the one administrator prompt belongs.</param>
    public async Task StartAsync(string mode, bool allowPrompt, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Installed)
            {
                if (_bundled && Linked)
                {
                    _logger.LogError("Optima Shield is missing from {Path}", _exePath);
                }
                return;
            }
            if (!Linked || NeedsDisclosure)
            {
                return;
            }

            _sessionMode = mode;
            _relaunched = false;
            _startedAt = _now();
            _told = null;
            _watch ??= new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            await LaunchAsync(mode, allowPrompt, ct).ConfigureAwait(false);
            // Counted from here and not from before the prompt: a player who took a while to answer
            // it has not made the module late.
            _startedAt = _now();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Protected play could not be started");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LaunchAsync(string mode, bool allowPrompt, CancellationToken ct)
    {
        if (Read() is { Kind: ShieldPresence.Protected, Elevated: true })
        {
            return;
        }

        if (await ElevatedAsync(allowPrompt, ct).ConfigureAwait(false))
        {
            var answer = await _elevation.SendAsync(
                new IpcRequest { Command = IpcCommand.LaunchShield, Args = { ["mode"] = mode } }, ct).ConfigureAwait(false);
            if (answer.Success)
            {
                return;
            }
            _logger.LogWarning("The helper did not start Optima Shield: {Error}", answer.Error);
        }

        // Without the rights. An instance that is already running stays, and this one exits at once.
        // Off the caller's thread: PLAY calls this from the window's, and starting a program can take a moment.
        if (await Task.Run(() => _run(_exePath, "mode=" + mode, false), ct).ConfigureAwait(false) is null)
        {
            _logger.LogWarning("Optima Shield could not be started from {Path}", _exePath);
        }
    }

    /// <summary>Whether the elevated helper is there to start the module, asking for it only when that is allowed.</summary>
    private async Task<bool> ElevatedAsync(bool allowPrompt, CancellationToken ct)
    {
        if (_elevation.IsConnected)
        {
            return true;
        }
        // A prompt that asks for another account's password is not put to anyone, and a No is not asked twice.
        if (!allowPrompt || _declined || !_isAdministrator())
        {
            return false;
        }

        var started = await _elevation.EnsureStartedAsync(ct).ConfigureAwait(false);
        _declined |= !started && _elevation.LastStartFailure == ElevationStartFailure.Declined;
        return started;
    }

    /// <summary>The session is over. The module is left to end by itself; it is only no longer restarted.</summary>
    public void SessionEnded() => _sessionMode = null;

    /// <summary>
    /// Looks at the module once: tells whoever listens when its state changed, and starts it again,
    /// once per session and never with a prompt, when it went silent while the session was running.
    /// </summary>
    public async Task CheckAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            // Measured without a sign, so a clock that was set back does not switch the watch off.
            if (_sessionMode is not { } mode || (_now() - _startedAt).Duration() < Settle)
            {
                return;
            }

            var state = Read();
            // A reason the module wrote before this session began belongs to an earlier run of it.
            if (state.Kind == ShieldPresence.Unprotected && ReadFile() is { } file
                && DateTimeOffset.FromUnixTimeSeconds(file.UpdatedUnix) < _startedAt - TimeSpan.FromSeconds(2))
            {
                state = new ShieldState(ShieldPresence.Stopped, false, string.Empty);
            }

            if (state.Kind == ShieldPresence.Stopped && !_relaunched)
            {
                _relaunched = true;
                _logger.LogWarning("Optima Shield went silent during a session; starting it once more");
                await LaunchAsync(mode, allowPrompt: false, CancellationToken.None).ConfigureAwait(false);
                // The second start gets the same time to settle as the first.
                _startedAt = _now();
                return;
            }
            if (state.Kind == ShieldPresence.Stopped)
            {
                // Started twice and still not there: the player is told, once, like any other reason.
                state = new ShieldState(ShieldPresence.Unprotected, false, NotRunning);
            }

            if (state != _told)
            {
                _told = state;
                Changed?.Invoke(state);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Checking Optima Shield failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>What the module last wrote, read for what it says and for how old it is.</summary>
    public ShieldState Read()
    {
        if (!Installed)
        {
            return new ShieldState(_bundled ? ShieldPresence.Removed : ShieldPresence.NoModule, false, string.Empty);
        }
        if (!Linked)
        {
            return new ShieldState(ShieldPresence.NotLinked, false, string.Empty);
        }
        if (NeedsDisclosure)
        {
            return new ShieldState(ShieldPresence.NeedsDisclosure, false, string.Empty);
        }

        var status = ReadFile();
        return status switch
        {
            { State: "unprotected" } => new ShieldState(ShieldPresence.Unprotected, status.Elevated, status.Message ?? string.Empty),
            // Without a sign: a status from "the future" after the clock was set back is as stale as an old one.
            { State: "protected" } when (_now() - DateTimeOffset.FromUnixTimeSeconds(status.UpdatedUnix)).Duration() <= Stale
                => new ShieldState(ShieldPresence.Protected, status.Elevated, string.Empty),
            _ => new ShieldState(ShieldPresence.Stopped, false, string.Empty),
        };
    }

    private StatusFile? ReadFile()
    {
        try
        {
            return JsonSerializer.Deserialize<StatusFile>(File.ReadAllText(_statusPath), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException)
        {
            // Not there, or being written this instant.
            return null;
        }
    }

    /// <summary>
    /// This PC's protected play key, public half, as the module prints it. Linking sends it to the
    /// bot. Null when there is no module or it could not say.
    /// </summary>
    public string? PublicKey()
        => Installed && _run(_exePath, "--print-public-key", true) is { Length: > 0 } key ? key.Trim() : null;

    /// <summary>
    /// Writes what the module needs to run: the linked account and the version of the disclosure
    /// the player read before linking. Called after a link that enrolled this PC.
    /// </summary>
    /// <param name="botUrl">The bot the link was made with. Only a development module reads it.</param>
    public void WriteLink(long accountId, string? botUrl)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_linkPath)!);
        File.WriteAllText(_linkPath, JsonSerializer.Serialize(new LinkFile(accountId, DisclosureVersion, botUrl), Json));
    }

    /// <summary>Removes the link file. The module starts nothing without it.</summary>
    public void RemoveLink()
    {
        try
        {
            File.Delete(_linkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove {Path}", _linkPath);
        }
    }

    /// <summary>Asks the running module to send its final report and exit, and waits a few seconds for that.</summary>
    public void Stop()
    {
        _sessionMode = null;
        if (Installed)
        {
            _run(_exePath, "--stop", true);
        }
    }

    public void Dispose()
    {
        _watch?.Dispose();
        _gate.Dispose();
    }

    private sealed record StatusFile(string? State, string? Message, bool Elevated, long UpdatedUnix);

    private sealed record LinkFile(
        long AccountId, int DisclosureVersion,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BotUrl);
}
