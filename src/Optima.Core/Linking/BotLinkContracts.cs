using System.Text.Json.Serialization;

namespace Optima.Core.Linking;

/// <summary>
/// What the desktop app sends to OptimaBot when it claims a link code. The code is the proof: it was
/// minted inside Discord for one user, it is single use and it expires, so the app never has to hold
/// a Discord credential to prove whose account this is.
/// </summary>
public sealed record BotLinkClaimRequest(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("accountId")] long AccountId,
    [property: JsonPropertyName("inGameName")] string InGameName,
    [property: JsonPropertyName("clientVersion")] string ClientVersion,
    // Optional: the tracker webhook the app copied out of Discord, when the user wants match and rank
    // posts after linking. Null links without tracking.
    [property: JsonPropertyName("webhookUrl")] string? WebhookUrl = null,
    // Optional: this PC's protected play key (base64 SubjectPublicKeyInfo, ECDSA P-256), as Optima
    // Shield prints it. The code proves the Discord identity, so this is the one moment a key can be
    // bound to it. Null links without protected play, which is what a build without the module sends.
    [property: JsonPropertyName("devicePublicKey")] string? DevicePublicKey = null);

/// <summary>The bot's answer to a claim: whether the link was written, and what to tell the user.</summary>
public sealed record BotLinkClaimResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("discordTag")] string? DiscordTag = null,
    [property: JsonPropertyName("playerName")] string? PlayerName = null,
    [property: JsonPropertyName("accountId")] long? AccountId = null,
    [property: JsonPropertyName("linkedAt")] DateTimeOffset? LinkedAt = null);

/// <summary>Whether an account is linked, and to whom (the app shows this beside the link button).</summary>
public sealed record BotLinkStatusResponse(
    [property: JsonPropertyName("linked")] bool Linked,
    [property: JsonPropertyName("discordTag")] string? DiscordTag = null,
    [property: JsonPropertyName("playerName")] string? PlayerName = null,
    [property: JsonPropertyName("accountId")] long? AccountId = null,
    [property: JsonPropertyName("linkedAt")] DateTimeOffset? LinkedAt = null,
    // Whether the link carries a device key, so Settings can say when a relink is needed.
    [property: JsonPropertyName("protectionReady")] bool ProtectionReady = false);

/// <summary>
/// Optima's tracker test: the app asks the bot to draw a sample report and deliver it to this webhook,
/// so the user can see where posts will land before a real match does. The webhook is the one being
/// tested, not a saved one, which is what lets the button work before an account is linked.
/// </summary>
public sealed record BotTrackerTestRequest(
    [property: JsonPropertyName("webhookUrl")] string WebhookUrl);

/// <summary>The bot's answer to a tracker test: whether the sample image was delivered, and what to say.</summary>
public sealed record BotTrackerTestResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// The link code's shape. One definition, shared by the bot that mints codes and any client that
/// validates what the user typed, so a code can never be well formed on one side and not the other.
/// The alphabet leaves out I, O, 0 and 1: a code is read off a Discord message and typed by hand.
/// </summary>
public static class LinkCode
{
    /// <summary>The code's prefix, kept short so the whole thing fits an ephemeral Discord message.</summary>
    public const string Prefix = "OPT-";

    /// <summary>Characters a code body may use (Crockford-style: no I, O, 0 or 1).</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Characters in the shortest code body, excluding the prefix. What the bot mints unless told otherwise.</summary>
    public const int BodyLength = 5;

    /// <summary>
    /// Characters in the longest code body. Five characters are 33 million codes, which a caller with
    /// many addresses can search; eight are a trillion. Both lengths are read, so the bot can move to
    /// the longer one once the apps in use accept it.
    /// </summary>
    public const int MaxBodyLength = 8;

    /// <summary>
    /// Parses what the user typed into the canonical uppercase form, or null when it cannot be a
    /// code at all. Dashes and spaces are ignored, so "opt 7f3kq" and "OPT-7F3KQ" are the same code.
    /// </summary>
    public static string? Normalize(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return null;
        }

        var compact = new System.Text.StringBuilder(typed.Length);
        foreach (var ch in typed.Trim().ToUpperInvariant())
        {
            if (ch is '-' or ' ' or '_')
            {
                continue;
            }
            compact.Append(ch);
        }

        var text = compact.ToString();
        // The prefix is dropped when it is there, which is unambiguous: O cannot appear in a code
        // body, so nothing that is only a body can look like a prefixed code. Users type both.
        if (text.StartsWith("OPT", StringComparison.Ordinal))
        {
            text = text[3..];
        }

        if (text.Length is < BodyLength or > MaxBodyLength || !text.All(Alphabet.Contains))
        {
            return null;
        }

        return Prefix + text;
    }
}

/// <summary>
/// The shape of a Discord webhook URL, shared the way the code shape is: the app refuses the typo
/// before it sends the claim, and the bot checks the same rule before it starts posting, so the two
/// can never disagree about what a tracker webhook is.
/// </summary>
public static class TrackerWebhook
{
    /// <summary>The hosts a Discord webhook URL can live on, legacy discordapp.com included.</summary>
    private static readonly string[] Hosts =
    [
        "discord.com",
        "discordapp.com",
        "canary.discord.com",
        "ptb.discord.com",
    ];

    /// <summary>
    /// Trims a typed URL into the form the bot stores, or null when it cannot be a Discord webhook.
    /// An empty string is null too: no webhook means linking without tracking, which is allowed.
    /// </summary>
    public static string? Normalize(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return null;
        }

        var value = typed.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        const string prefix = "/api/webhooks/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        // A webhook path is an id and a token, both non-empty; anything else is a 404 in waiting.
        var parts = uri.AbsolutePath[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0 ? value : null;
    }
}
