using System.Text.RegularExpressions;

namespace Optima.Core.Health;

/// <summary>Whose name and machine a text is scrubbed of.</summary>
public sealed record RedactionIdentity(string UserName, string MachineName)
{
    public static RedactionIdentity Current => new(Environment.UserName, Environment.MachineName);
}

/// <summary>
/// Makes a text safe to hand to someone else: anything token-shaped, user profile paths, the
/// Windows user name and the machine name are masked (§17). Everything that leaves the app as
/// text goes through here (the log export, a copied report, a crash zip, the support archive), so
/// the promise that exports are redacted is kept in one place. A stack trace is full of profile
/// paths, which is why the token mask alone stopped being enough once exceptions were exported.
/// </summary>
public static partial class Redactor
{
    [GeneratedRegex(@"(?i)(token|bearer|password|secret|api[_-]?key)\s*[=:]\s*\S+")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"(?i)[A-Z]:\\Users\\[^\\/\r\n""]+")]
    private static partial Regex UserProfilePath();

    public static string Redact(string text) => Redact(text, RedactionIdentity.Current);

    public static string Redact(string text, RedactionIdentity identity)
    {
        var redacted = SecretPattern().Replace(text, "$1=[REDACTED]");
        redacted = UserProfilePath().Replace(redacted,
            m => m.Value[..(m.Value.IndexOf("Users", StringComparison.OrdinalIgnoreCase) + 5)] + @"\[user]");
        redacted = Mask(redacted, identity.UserName, "[user]");
        return Mask(redacted, identity.MachineName, "[machine]");
    }

    /// <summary>A one-letter name is left alone: masking it would eat that letter out of every word.</summary>
    private static string Mask(string text, string name, string mask)
        => string.IsNullOrWhiteSpace(name) || name.Length < 2
            ? text
            : text.Replace(name, mask, StringComparison.OrdinalIgnoreCase);
}
