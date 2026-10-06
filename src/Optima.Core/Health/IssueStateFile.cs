using Microsoft.Extensions.Logging;
using Optima.Core.Configuration;

namespace Optima.Core.Health;

/// <summary>An issue the user said never to raise again.</summary>
public sealed record IgnoredIssue(string Key, string Title, DateTimeOffset At);

/// <summary>What the issue list remembers between runs.</summary>
public sealed record IssueStateData
{
    public IReadOnlyList<IgnoredIssue> Ignored { get; init; } = [];

    /// <summary>
    /// The repairs that were run. They outlive the run because the policy counts them: a repair
    /// that did not help yesterday evening is not tried again this morning as if it were new.
    /// </summary>
    public IReadOnlyList<RepairAttempt> Attempts { get; init; } = [];
}

/// <summary>
/// The file behind <see cref="IssueStateData"/>. The issues themselves are not kept: they are read
/// off the running log and the checks, and a problem that is still there shows up again by itself.
/// What has to survive a restart is what the user decided about them.
/// </summary>
public sealed class IssueStateFile
{
    private readonly JsonStore _store;
    private readonly ILogger<IssueStateFile> _logger;

    public IssueStateFile(JsonStore store, string path, ILogger<IssueStateFile> logger)
    {
        _store = store;
        Path = path;
        _logger = logger;
    }

    public string Path { get; }

    public IssueStateData Load()
    {
        try
        {
            return _store.Load<IssueStateData>(Path) ?? new IssueStateData();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "The issue state could not be read; starting without it");
            return new IssueStateData();
        }
    }

    public async Task SaveAsync(IssueStateData data, CancellationToken ct = default)
    {
        try
        {
            await _store.SaveAsync(Path, data, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember an ignore is not worth an error of its own.
            _logger.LogDebug(ex, "The issue state could not be saved");
        }
    }
}
