namespace Optima.Core.Models;

/// <summary>
/// An error surfaced to the user (§24): plain-language message + suggested fixes + stable code, with raw developer
/// details kept behind an expander.
/// </summary>
public sealed record UserFriendlyError
{
    public required string Code { get; init; }
    public required string Title { get; init; }
    public string Explanation { get; init; } = string.Empty;
    public IReadOnlyList<string> SuggestedFixes { get; init; } = [];
    public string DeveloperDetails { get; init; } = string.Empty;
}

/// <summary>Exception that already carries a user-friendly presentation.</summary>
public sealed class OptimaException : Exception
{
    public UserFriendlyError Error { get; }

    public OptimaException(UserFriendlyError error, Exception? inner = null)
        : base(error.Title, inner)
    {
        Error = error;
    }

    public static OptimaException From(string code, string title, string explanation, Exception? inner = null, params string[] fixes)
        => new(new UserFriendlyError
        {
            Code = code,
            Title = title,
            Explanation = explanation,
            SuggestedFixes = fixes,
            DeveloperDetails = inner?.ToString() ?? string.Empty,
        }, inner);
}
