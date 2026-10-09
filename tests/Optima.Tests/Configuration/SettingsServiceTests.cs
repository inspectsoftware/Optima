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

    [Fact]
    public async Task AnUpdateThatChangesNothingIsNotASave()
    {
        // What every start does: the selected profile is "selected" again, and nothing is different.
        var service = CreateService();
        await service.UpdateSettingsAsync(s => s with { SelectedProfileName = "Competitive" });
        var written = File.GetLastWriteTimeUtc(_paths.ConfigFile);
        var raised = 0;
        service.SettingsChanged += (_, _) => raised++;

        await service.UpdateSettingsAsync(s => s with { SelectedProfileName = "Competitive" });

        Assert.Equal(0, raised);
        Assert.Equal(written, File.GetLastWriteTimeUtc(_paths.ConfigFile));

        await service.UpdateSettingsAsync(s => s with { SelectedProfileName = "Balanced" });

        Assert.Equal(1, raised);
        Assert.Equal("Balanced", (await CreateService().GetSettingsAsync()).SelectedProfileName);
    }

    [Fact]
    public async Task AFirstStartWritesItsMigrationEvenWhenNothingElseChanges()
    {
        // The flag has to reach the file at the first start: it is what lets a self-hoster edit
        // the address by hand afterwards and keep it.
        await _store.SaveAsync(_paths.ConfigFile, new AppSettings { DiscordBotUrl = "http://127.0.0.1:5099" });
        var service = CreateService();

        await service.UpdateSettingsAsync(s => s with { SelectedProfileName = s.SelectedProfileName });

        var onDisk = await _store.LoadAsync<AppSettings>(_paths.ConfigFile);
        Assert.True(onDisk!.DiscordBotUrlMigrated);
        Assert.Equal(BotLinkClient.DefaultBaseUrl, onDisk.DiscordBotUrl);
    }

    [Fact]
    public async Task AnUpdateWhoseSaveFailedIsStillAnUpdateTheNextTime()
    {
        var service = CreateService();
        await service.UpdateSettingsAsync(s => s with { SelectedProfileName = "Competitive" });
        var raised = 0;
        service.SettingsChanged += (_, _) => raised++;

        // Held open without sharing, the file cannot be replaced: the save gives up and throws.
        using (new FileStream(_paths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => service.UpdateSettingsAsync(s => s with { EnableWatchMode = true }));
        }
        Assert.Equal(0, raised);

        await service.UpdateSettingsAsync(s => s with { EnableWatchMode = true });

        Assert.Equal(1, raised);
        Assert.True((await CreateService().GetSettingsAsync()).EnableWatchMode);
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
