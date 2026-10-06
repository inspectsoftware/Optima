using System.Globalization;
using System.Windows;
using Optima.Core.Linking;

namespace Optima.App.Views;

/// <summary>
/// The link dialog: the user types the code /link sent them, and Optima redeems it with the Critical Ops
/// account it has on file.
///
/// The account is the app's, never the user's to retype here: what is being proven is that whoever holds
/// the Discord account (the code) also holds this Optima install and its player (the settings). A typo in
/// the code is answered by the bot, and only a link the bot confirms closes the dialog with a result.
/// </summary>
public partial class DiscordLinkWindow : Window
{
    private readonly BotLinkClient _client;
    private readonly string _baseUrl;
    private readonly long? _accountId;
    private readonly string _inGameName;
    private readonly string? _webhook;

    /// <summary>The bot's answer once a link was written, or null when the dialog was dismissed.</summary>
    public BotLinkClaimResponse? Result { get; private set; }

    public DiscordLinkWindow(
        BotLinkClient client,
        string baseUrl,
        long? accountId,
        string inGameName,
        BotLinkStatusResponse? status,
        string? webhook = null)
    {
        InitializeComponent();
        _client = client;
        _baseUrl = baseUrl;
        _accountId = accountId;
        _inGameName = inGameName;
        _webhook = webhook;

        TrackerText.Text = webhook is null
            ? "no tracker webhook: linking without match posts"
            : "new matches and rank changes will be posted to your tracker webhook";
        IdentityText.Text = accountId is { } id
            ? $"Optima will link account {id.ToString(CultureInfo.InvariantCulture)}"
                + (inGameName.Length > 0 ? $" ({inGameName}) to whoever owns the code." : " to whoever owns the code.")
            : inGameName.Length > 0
                ? $"Optima has no account id saved, so it will link {inGameName} to whoever owns the code."
                : "Optima has no in-game name or account id saved yet, so there is nothing to link. Set your name on the Settings page first.";

        if (status is { Linked: true })
        {
            StatusText.Text = $"This account is already linked to {status.PlayerName} as {status.DiscordTag}. "
                + "Linking again replaces that link; to remove it, run the bot's unlink command instead.";
            CopyButton.Visibility = Visibility.Visible;
        }
        else if (accountId is null && inGameName.Length == 0)
        {
            LinkButton.IsEnabled = false;
        }

        Loaded += (_, _) => CodeBox.Focus();
    }

    private async void OnLink(object sender, RoutedEventArgs e)
    {
        var normalized = LinkCode.Normalize(CodeBox.Text);
        if (normalized is null)
        {
            StatusText.Text = "That does not look like a link code. They look like OPT-7F3KQ; run /link in Discord if you have not yet.";
            return;
        }

        LinkButton.IsEnabled = false;
        StatusText.Text = "Asking OptimaBot…";
        try
        {
            var result = await _client.ClaimAsync(
                _baseUrl, normalized, _accountId ?? 0, _inGameName, _webhook, CancellationToken.None);

            // A failure the user can act on (no code, expired code, unknown player) comes back as a
            // sentence with ok: false, and the dialog stays open so they can fix it.
            if (result.Ok && result.Value is { Ok: true } claim)
            {
                Result = claim;
                StatusText.Text = claim.Message;
                DialogResult = true;
                return;
            }

            StatusText.Text = result.Ok && result.Value is { } answered
                ? answered.Message
                : result.Message;
            CopyButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            StatusText.Text = "The link could not be completed: " + ex.Message;
        }
        finally
        {
            LinkButton.IsEnabled = true;
        }
    }

    /// <summary>Opens the Optima server, because /link lives in Discord and the code is read there.</summary>
    private void OnOpenDiscord(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://discord.gg/bGuJ4tvsF7",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Discord could not be opened: " + ex.Message;
        }
    }
}
