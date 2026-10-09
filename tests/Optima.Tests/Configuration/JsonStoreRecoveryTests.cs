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

    [Fact]
    public async Task AFileLostBetweenTheTwoRenamesOfASave_ComesBackFromItsBackup()
    {
        await SaveTwice();
        // What a process that ends mid-replace leaves: the backup, and nothing under the name.
        File.Delete(File_);

        Assert.Equal("the earlier save", (await _store.LoadAsync<AppSettings>(File_))?.PlayerIgn);
        Assert.True(File.Exists(File_));

        File.Delete(File_);
        Assert.Equal("the earlier save", _store.Load<AppSettings>(File_)?.PlayerIgn);
    }

    [Fact]
    public async Task ADeletedFileTakesItsBackupWithIt()
    {
        await SaveTwice();
        _store.Delete(File_);
        Assert.False(File.Exists(File_ + ".bak"));
        Assert.Null(await _store.LoadAsync<AppSettings>(File_));

        // The next file under that name is a new one. When it turns out damaged, there is nothing
        // of the deleted one left to come back in its place.
        await _store.SaveAsync(File_, new AppSettings { PlayerIgn = "a new file" });
        File.WriteAllText(File_, "not json at all");

        Assert.Null(await _store.LoadAsync<AppSettings>(File_));
    }

    [Fact]
    public async Task ASaveWaitsOutAFileThatIsHeldOpenForAMoment()
    {
        await _store.SaveAsync(File_, new AppSettings { PlayerIgn = "the earlier save" });

        // What a virus scanner does right after a write: the file is open, and not for long.
        Task save;
        using (new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            save = _store.SaveAsync(File_, new AppSettings { PlayerIgn = "the latest save" });
            await Task.Delay(50);
        }
        await save;

        Assert.Equal("the latest save", _store.Load<AppSettings>(File_)?.PlayerIgn);
    }

    [Fact]
    public async Task ASaveNeverComesBackToTheCallersThread()
    {
        // The window's thread in miniature: a context that counts what is sent back to it.
        var caller = new CountingContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(caller);
        Task save;
        try
        {
            // Small enough to fit the file stream's buffer, which is the save that used to finish
            // (the rename, and the release of the store-wide gate) on the thread that started it.
            save = _store.SaveAsync(File_, new AppSettings { PlayerIgn = "a small save" });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await save;

        Assert.Equal(0, caller.Posts);
        Assert.Equal("a small save", _store.Load<AppSettings>(File_)?.PlayerIgn);
    }

    private sealed class CountingContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            base.Post(d, state);
        }
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
