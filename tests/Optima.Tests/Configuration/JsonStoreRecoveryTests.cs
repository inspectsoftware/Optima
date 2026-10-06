using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Configuration;

public sealed class JsonStoreRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "optima-store-" + Guid.NewGuid().ToString("N"));
    private readonly JsonStore _store = new(NullLogger<JsonStore>.Instance);

    private string File_ => Path.Combine(_directory, "config.json");

    private async Task SaveTwice()
    {
        // The second save is what creates the backup: it holds the first save's values.
        await _store.SaveAsync(File_, new AppSettings { PlayerIgn = "the earlier save" });
        await _store.SaveAsync(File_, new AppSettings { PlayerIgn = "the latest save" });
    }

    private bool WasSetAside() => Directory.EnumerateFiles(_directory, "config.json.corrupt-*").Any();

    [Fact]
    public async Task ACorruptFileIsRecoveredFromItsBackupBeforeAnySave()
    {
        await SaveTwice();
        File.WriteAllText(File_, "{ \"playerIgn\": \"cut off in the midd");

        var loaded = await _store.LoadAsync<AppSettings>(File_);

        // One save is lost, not the file.
        Assert.Equal("the earlier save", loaded?.PlayerIgn);
        Assert.True(WasSetAside());

        // The recovered values are back on disk already. Saving now rotates them into the backup,
        // where before it rotated defaults over the last good copy.
        await _store.SaveAsync(File_, loaded! with { PlayerIgn = "saved after the recovery" });
        Assert.Equal("the earlier save", _store.Load<AppSettings>(File_ + ".bak")?.PlayerIgn);
        Assert.Equal("saved after the recovery", _store.Load<AppSettings>(File_)?.PlayerIgn);
    }

    [Fact]
    public async Task TheStartupReadRecoversTheSameWay()
    {
        await SaveTwice();
        File.WriteAllText(File_, "not json at all");

        Assert.Equal("the earlier save", _store.Load<AppSettings>(File_)?.PlayerIgn);
    }

    [Fact]
    public async Task ACorruptFileWithNoBackupStartsAfresh()
    {
        await _store.SaveAsync(File_, new AppSettings { PlayerIgn = "the only save" });
        File.WriteAllText(File_, "not json at all");

        Assert.Null(await _store.LoadAsync<AppSettings>(File_));
        Assert.True(WasSetAside());
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public async Task ABackupThatIsDamagedTooIsNotTrusted()
    {
        await SaveTwice();
        File.WriteAllText(File_, "not json at all");
        File.WriteAllText(File_ + ".bak", "nor is this");

        Assert.Null(await _store.LoadAsync<AppSettings>(File_));
        Assert.True(WasSetAside());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
