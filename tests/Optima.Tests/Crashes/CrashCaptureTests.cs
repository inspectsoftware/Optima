using Optima.Core.Crashes;
using Xunit;

namespace Optima.Tests.Crashes;

public sealed class CrashSignalsTests
{
    private const string Package = "com.criticalforceentertainment.criticalops";

    [Fact]
    public void FatalExceptionIsDetectedAndExcerpted()
    {
        string[] lines =
        [
            "08-30 01:00:00.000  427  467 I ActivityManager: Start proc 1944:" + Package + "/u0a52",
            "08-30 01:05:00.000 1944 1944 E AndroidRuntime: FATAL EXCEPTION: main",
            "08-30 01:05:00.001 1944 1944 E AndroidRuntime: java.lang.OutOfMemoryError",
            "08-30 01:05:00.100  427  467 I ActivityManager: Process " + Package + " has died",
            "08-30 01:05:01.000  123  456 I SomethingElse: unrelated noise",
        ];

        var evidence = CrashSignals.Extract(lines, Package);

        Assert.True(evidence.FatalSeen);
        Assert.True(CrashSignals.ShouldCapture(evidence));
        Assert.Contains(evidence.ExcerptLines, l => l.Contains("FATAL EXCEPTION"));
        Assert.Contains(evidence.ExcerptLines, l => l.Contains("has died"));
        Assert.DoesNotContain(evidence.ExcerptLines, l => l.Contains("unrelated noise"));
    }

    [Fact]
    public void QuietExitProducesNoCapture()
    {
        string[] lines =
        [
            "08-30 01:00:00.000  427  467 I ActivityManager: Start proc 1944:" + Package + "/u0a52",
            "08-30 01:20:00.000 1026 1180 I LaunchGamePcsHandler: Force stopping " + Package,
            "08-30 01:20:00.100  427 1123 I ActivityManager: Force stopping " + Package + " appid=10052",
        ];

        var evidence = CrashSignals.Extract(lines, Package);

        Assert.False(evidence.FatalSeen);
        Assert.True(evidence.ForceStopSeen);
        Assert.False(CrashSignals.ShouldCapture(evidence));
    }

    [Fact]
    public void EmptyTailIsHarmless()
    {
        var evidence = CrashSignals.Extract([], Package);
        Assert.Equal(CrashEvidence.Empty, evidence);
        Assert.False(CrashSignals.ShouldCapture(evidence));
    }

    [Fact]
    public void ExcerptIsCapped()
    {
        var lines = Enumerable.Range(0, 500)
            .Select(i => $"08-30 01:00:{i % 60:00}.000 1944 1944 I {Package}: line {i}")
            .ToArray();

        var evidence = CrashSignals.Extract(lines, Package, maxExcerptLines: 100);

        Assert.Equal(100, evidence.ExcerptLines.Count);
        Assert.EndsWith("line 499", evidence.ExcerptLines[^1]);
    }
}

public sealed class GpgLogReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optima-test-" + Guid.NewGuid().ToString("N"));
    private string LogsDir => Path.Combine(_root, "Logs");

    public GpgLogReaderTests() => Directory.CreateDirectory(LogsDir);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void MissingFolderReturnsEmpty()
    {
        var reader = new GpgLogReader(Path.Combine(_root, "does-not-exist"));
        Assert.False(reader.LogsFolderExists);
        Assert.Empty(reader.ReadRecentSerialLines());
        Assert.Empty(reader.ListMinidumpNames());
    }

    [Fact]
    public void ReadsLiveFileTail()
    {
        File.WriteAllLines(Path.Combine(LogsDir, "AndroidSerial.log"),
            Enumerable.Range(1, 50).Select(i => $"line {i}"));

        var lines = new GpgLogReader(LogsDir).ReadRecentSerialLines(maxLines: 10);

        Assert.Equal(10, lines.Count);
        Assert.Equal("line 50", lines[^1]);
        Assert.Equal("line 41", lines[0]);
    }

    [Fact]
    public void ShortLiveFilePrependsNewestBackup()
    {
        File.WriteAllLines(Path.Combine(LogsDir, "AndroidSerial-bkup-20260830-0001.log"),
            ["old rotation A", "old rotation B"]);
        // Newest backup written last so its timestamp wins.
        Thread.Sleep(30);
        File.WriteAllLines(Path.Combine(LogsDir, "AndroidSerial-bkup-20260830-0002.log"),
            ["new rotation A", "new rotation B"]);
        File.WriteAllLines(Path.Combine(LogsDir, "AndroidSerial.log"), ["fresh 1"]);

        var lines = new GpgLogReader(LogsDir).ReadRecentSerialLines(maxLines: 10);

        Assert.Equal(["new rotation A", "new rotation B", "fresh 1"], lines);
    }

    [Fact]
    public void MinidumpNamesComeFromSiblingCrashReporting()
    {
        var crashDir = Path.Combine(_root, "CrashReporting");
        Directory.CreateDirectory(crashDir);
        File.WriteAllBytes(Path.Combine(crashDir, "dump-1.dmp"), new byte[2048]);

        var names = new GpgLogReader(LogsDir).ListMinidumpNames();

        var entry = Assert.Single(names);
        Assert.StartsWith("dump-1.dmp (2 KB", entry);
    }
}
