using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Monitoring.Metrics;
using Optima.Tests.Launch;
using Xunit;

namespace Optima.Tests.Monitoring;

public sealed class EtwMetricsProviderClientTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "optima-etw-" + Guid.NewGuid().ToString("N"));
    private readonly FakeElevationBroker _broker = new();
    private readonly SettingsService _settings;

    public EtwMetricsProviderClientTests()
    {
        var paths = new AppPaths(_tempRoot);
        paths.EnsureCreated();
        _settings = new SettingsService(paths, new JsonStore(NullLogger<JsonStore>.Instance), NullLogger<SettingsService>.Instance);
    }

    private EtwMetricsProviderClient Create() => new(_broker, _settings, NullLogger<EtwMetricsProviderClient>.Instance);

    [Fact]
    public async Task ADeclinedPrompt_IsNotRaisedAgainWhenCaptureStarts()
    {
        _broker.LastStartFailure = ElevationStartFailure.Declined;
        await using var capture = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.StartAsync([4242]));

        Assert.Equal(0, _broker.Prompts);
    }

    [Fact]
    public async Task TheHelperIsAskedForAhead_OnlyWhenCaptureWillUseIt()
    {
        await using var capture = Create();

        await capture.EnsureHelperAsync();
        Assert.Equal(1, _broker.Prompts);

        // Already there, or capture switched off: nothing to ask for.
        _broker.IsConnected = true;
        await capture.EnsureHelperAsync();
        _broker.IsConnected = false;
        await _settings.SaveSettingsAsync(new AppSettings { EnableFrametimeCapture = false });
        await capture.EnsureHelperAsync();
        Assert.Equal(1, _broker.Prompts);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
