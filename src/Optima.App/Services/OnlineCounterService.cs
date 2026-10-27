using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Optima.Core.Configuration;
using Optima.Core.Linking;
using Microsoft.Extensions.Logging;

namespace Optima.App.Services;

/// <summary>
/// The "N online" line in the sidebar: how many copies of Optima are open right now.
///
/// Once a minute this run tells OptimaBot it is still open, with an id made up here at start and never
/// saved, and the bot answers how many runs it heard from in the last few minutes. No account, name or
/// machine id goes along. While the bot cannot be reached the line hides instead of showing a stale number.
/// </summary>
public sealed partial class OnlineCounterService : ObservableObject, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly CancellationTokenSource _stop = new();
    private readonly BotLinkClient _bot;
    private readonly SettingsService _settings;
    private readonly ILogger<OnlineCounterService> _logger;
    private int _misses;

    public OnlineCounterService(BotLinkClient bot, SettingsService settings, ILogger<OnlineCounterService> logger)
    {
        _bot = bot;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Runs counted by the bot, this one included; null until the first answer and after two missed ones.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private int? _online;

    public string Text => Online is { } n ? $"{n:N0} online" : string.Empty;

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            var baseUrl = _settings.Current?.DiscordBotUrl;
            BotLinkCallResult<BotOnlineResponse> result;
            try
            {
                result = await _bot.PingOnlineAsync(
                    string.IsNullOrWhiteSpace(baseUrl) ? BotLinkClient.DefaultBaseUrl : baseUrl, _instanceId, _stop.Token);
            }
            catch (Exception ex)
            {
                // The client catches network and JSON failures; anything else (an odd charset from a
                // captive portal) is a miss too, not the end of the loop with a stale number on screen.
                result = BotLinkCallResult<BotOnlineResponse>.Fail(ex.Message);
            }
            int? online = null;
            if (result.Ok && result.Value is { Online: > 0 } answer)
            {
                _misses = 0;
                online = answer.Online;
            }
            else if (++_misses < 2)
            {
                // One lost ping keeps the last number: Railway restarts and Wi-Fi blips are short.
                online = Online;
            }
            else if (_misses == 2)
            {
                _logger.LogDebug("Online counter hidden: {Message}", result.Message);
            }

            Application.Current?.Dispatcher.BeginInvoke(() => Online = online);
        }
        while (!_stop.IsCancellationRequested && await WaitAsync(timer));
    }

    private async Task<bool> WaitAsync(PeriodicTimer timer)
    {
        try
        {
            return await timer.WaitForNextTickAsync(_stop.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // Cancel only: the loop may still be reading the token, and a disposed source throws on that.
    public void Dispose() => _stop.Cancel();
}
