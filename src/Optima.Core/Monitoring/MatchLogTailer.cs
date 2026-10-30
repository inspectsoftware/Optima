using System.Text;
using System.Text.RegularExpressions;

namespace Optima.Core.Monitoring;

/// <summary>
/// Which playlist a match belongs to. The game never names it; Ranked is inferred from an analytics
/// event the game sends as a ranked match starts loading (see <see cref="MatchLogTailer"/>), and
/// everything else is Casual, which covers TDM, casual defuse and custom alike.
/// </summary>
public enum MatchMode
{
    Unknown,
    Ranked,
    Casual,
}

public enum MatchPhase
{
    /// <summary>No load line seen since the tailer started.</summary>
    Unknown,
    InMenu,
    InMatch,
}

/// <summary>Where the player is inside the game, as read from its own load lines.</summary>
public sealed record MatchState(MatchPhase Phase, string Map = "", MatchMode Mode = MatchMode.Unknown, DateTimeOffset? Since = null)
{
    public static MatchState None { get; } = new(MatchPhase.Unknown);
}

/// <summary>
/// Read-only tail of Google Play Games' logcat file (AndroidSerial.log) for the Critical Ops load lines
/// "[LoadTiming] MENU -> GAMEPLAY (Raid)" and "[LoadTiming] GAMEPLAY -> MENU (Menu)". It only reads the
/// file GPG writes on its own; nothing touches the game's process or traffic.
/// </summary>
public sealed partial class MatchLogTailer
{
    private const int TailBytes = 64;

    private readonly string _path;
    private readonly long _maxBytesPerPoll;

    private long _offset = -1; // -1: never read, so the next poll only finds the end.

    // Ranked inference, all in logcat time (the lines carry month-day and time, no year).
    private string? _gamePid;
    private DateTime? _lastSubmitEvent;
    private DateTime? _lastMenu;
    private byte[] _tail = [];

    public MatchLogTailer(string path, long maxBytesPerPoll = 256 * 1024)
    {
        _path = path;
        _maxBytesPerPoll = maxBytesPerPoll;
    }

    public MatchState State { get; private set; } = MatchState.None;

    /// <summary>Forgets the position and the state, so the next poll starts again from the end.</summary>
    public void Reset()
    {
        _offset = -1;
        _tail = [];
        _gamePid = null;
        _lastSubmitEvent = null;
        _lastMenu = null;
        State = MatchState.None;
    }

    /// <summary>Reads what was appended since the last poll; true when <see cref="State"/> changed.</summary>
    public bool Poll(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return false;
            }
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;

            if (_offset < 0)
            {
                // First read: history before Optima looked is an old match, not this one.
                MoveTo(stream, length);
                return false;
            }

            // Rotation renames the file away and starts a new one. Every AndroidSerial.log begins with
            // the same U-Boot banner, so the head says nothing; the bytes just before our offset do:
            // a shorter file, or different bytes there, mean this is not the file we were following.
            if (length < _offset || !ReadBefore(stream, _offset).AsSpan().SequenceEqual(_tail))
            {
                MoveTo(stream, 0);
            }

            var start = Math.Max(_offset, length - _maxBytesPerPoll);
            var skipFirstLine = start > _offset; // jumped into the middle of a line
            if (start >= length)
            {
                return false;
            }

            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[length - start];
            stream.ReadExactly(buffer);

            // Only whole lines are consumed; a line still being written is read again next poll.
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n');
            if (lastNewline < 0)
            {
                if (skipFirstLine)
                {
                    MoveTo(stream, length);
                }
                return false;
            }
            MoveTo(stream, start + lastNewline + 1);

            var text = Encoding.UTF8.GetString(buffer, 0, lastNewline);
            var lines = text.Split('\n');
            var before = State;
            for (var i = skipFirstLine ? 1 : 0; i < lines.Length; i++)
            {
                Apply(lines[i], now);
            }
            return before != State;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Applies one log line to <see cref="State"/>; public so the parse is testable alone.</summary>
    public void Apply(string line, DateTimeOffset now)
    {
        var head = Head().Match(line);
        var at = head.Success ? ParseLogTime(head.Groups["time"].Value, now) : null;
        var pid = head.Success ? head.Groups["pid"].Value : null;
        if (pid is not null && UnityTag().IsMatch(line))
        {
            // Any line the game writes names its process, so a watch that starts mid-session
            // still knows whose analytics events to trust before the first load line.
            _gamePid = pid;
        }

        if (line.Contains("ApiSubmitEvent", StringComparison.Ordinal))
        {
            // The game's analytics SDK. Only its own process counts; other apps use the SDK too.
            if (SubmitEvent().IsMatch(line) && pid is not null && pid == _gamePid)
            {
                _lastSubmitEvent = at;
            }
            return;
        }

        if (!line.Contains("[LoadTiming]", StringComparison.Ordinal))
        {
            return;
        }
        var m = LoadTiming().Match(line);
        if (!m.Success)
        {
            return;
        }
        var to = m.Groups["to"].Value;
        if (to.Equals("GAMEPLAY", StringComparison.OrdinalIgnoreCase))
        {
            State = new MatchState(MatchPhase.InMatch, m.Groups["map"].Value.Trim(), ModeOf(line, at), now);
        }
        else if (to.Equals("MENU", StringComparison.OrdinalIgnoreCase))
        {
            _lastMenu = at;
            State = new MatchState(MatchPhase.InMenu, Since: now);
        }
    }

    /// <summary>
    /// Ranked when the game sent an analytics event within 15 s before (or 1 s after) the match began
    /// loading, and after it last returned to the menu. On the owner's logs (Oct 2026: 8 ranked, 11
    /// casual starts) that separated them every time; the menu guard drops the event a finished match
    /// sends just before it ends. The game logs no mode name, so a game update can break this.
    /// </summary>
    private MatchMode ModeOf(string line, DateTime? at)
    {
        var total = TotalSeconds().Match(line);
        if (at is null || !total.Success
            || !double.TryParse(total.Groups["s"].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return MatchMode.Unknown;
        }
        var loadStart = at.Value - TimeSpan.FromSeconds(seconds);
        return _lastSubmitEvent is { } ev
               && ev >= loadStart - TimeSpan.FromSeconds(15) && ev <= loadStart + TimeSpan.FromSeconds(1)
               && (_lastMenu is null || ev > _lastMenu)
            ? MatchMode.Ranked
            : MatchMode.Casual;
    }

    // ponytail: logcat has no year; a match loading across New Year's midnight reads as Casual.
    private static DateTime? ParseLogTime(string text, DateTimeOffset now)
        => DateTime.TryParseExact($"{now.Year}-{text}", "yyyy-MM-dd HH:mm:ss.fff",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
            ? t
            : null;

    private void MoveTo(FileStream stream, long offset)
    {
        _offset = offset;
        _tail = ReadBefore(stream, offset);
    }

    /// <summary>Up to <see cref="TailBytes"/> bytes ending at <paramref name="offset"/>: what identifies the file we follow.</summary>
    private static byte[] ReadBefore(FileStream stream, long offset)
    {
        var tail = new byte[Math.Min(TailBytes, offset)];
        stream.Seek(offset - tail.Length, SeekOrigin.Begin);
        stream.ReadExactly(tail);
        return tail;
    }

    [GeneratedRegex(@"^(?<time>\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\s+(?<pid>\d+)\s+\d+\s")]
    private static partial Regex Head();

    [GeneratedRegex(@"\sI Singular: ApiManager \[api\] - api = com\.singular\.sdk\.internal\.ApiSubmitEvent\s*$")]
    private static partial Regex SubmitEvent();

    [GeneratedRegex(@"^\S+ \S+\s+\d+\s+\d+ [VDIWEF] Unity\s*:")]
    private static partial Regex UnityTag();

    [GeneratedRegex(@"\):\s*total (?<s>[\d.]+) s")]
    private static partial Regex TotalSeconds();

    // The map is capped and limited to plain characters: it goes onto a public card.
    [GeneratedRegex(@"\[LoadTiming\]\s*(?<from>[A-Z_]+)\s*->\s*(?<to>[A-Z_]+)\s*\((?<map>[A-Za-z0-9 _'-]{0,32})\)")]
    private static partial Regex LoadTiming();
}
