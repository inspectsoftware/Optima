using Optima.Core.Configuration;
using Optima.Core.Linking;
using Optima.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Optima.Tests.Configuration;

/// <summary>
/// The one-time move off the old built-in bot address. Every configuration written before the community
/// bot existed carries http://127.0.0.1:5099, which was a default and not a choice, and no user machine
/// has a bot there: a claim against it fails with "Nothing answered". What must not happen is the same
/// rule eating an address a self-hoster typed on purpose, so the flag is part of the contract.
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "optima-settings-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly JsonStore _store = new(NullLogger<JsonStore>.Instance);

    public SettingsServiceTests()
    {
        _paths = new AppPaths(_tempRoot);
        _paths.EnsureCreated();
    }

    private SettingsService CreateService() => new(_paths, _store, NullLogger<SettingsService>.Instance);

    [Fact]
    public async Task AConfigurationFromBeforeTheCommunityBotGetsTheCommunityAddress()
    {
        await _store.SaveAsync(_paths.ConfigFile, new AppSettings { DiscordBotUrl = "http://127.0.0.1:5099" });

        var settings = await CreateService().GetSettingsAsync();

        Assert.Equal(BotLinkClient.DefaultBaseUrl, settings.DiscordBotUrl);
        Assert.True(settings.DiscordBotUrlMigrated);
    }

    [Fact]
    public async Task ALocalAddressTypedAfterTheMigrationIsKept()
    {
        // What a self-hoster does: takes the community default, then points the field at their own bot on
        // this machine. The migration must not undo that on the next start.
        await CreateService().UpdateSettingsAsync(s => s with { DiscordBotUrl = "http://127.0.0.1:5099" });

        var settings = await CreateService().GetSettingsAsync();

        Assert.Equal("http://127.0.0.1:5099", settings.DiscordBotUrl);
    }

    [Fact]
    public async Task AConfigurationThatNeverChoseAnAddressLandsOnTheCommunityBot()
    {
        // No config.json at all: a fresh install. The shipped default is already the community bot, and
        // the flag is set so a later explicit choice is never overruled.
        var settings = await CreateService().GetSettingsAsync();

        Assert.Equal(BotLinkClient.DefaultBaseUrl, settings.DiscordBotUrl);
        Assert.True(settings.DiscordBotUrlMigrated);
        Assert.StartsWith("https://", settings.DiscordBotUrl);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }
}
