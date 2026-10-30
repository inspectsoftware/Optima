using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Launch;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Optima.Platform.Windows.NativeMethods;
using Optima.Tests.Launch;
using Xunit;

namespace Optima.Tests.Boost;

public sealed class WifiScanTests
{
    private sealed class FakeWifi : IWifiScanPause
    {
        public IReadOnlyList<WifiInterfaceHold>? Interfaces { get; set; } = [new("Wi-Fi 6E", true, false)];
        public Exception? Fails { get; set; }
        public int Holds { get; private set; }
        public int Releases { get; private set; }
        public bool Open { get; private set; }

        public IReadOnlyList<WifiInterfaceHold>? Hold()
        {
            Holds++;
            Open = true;
            return Fails is { } fails ? throw fails : Interfaces;
        }

        public void Release()
        {
            Releases++;
            Open = false;
        }
    }

    private readonly FakeWifi _wifi = new();
    private readonly GamePresenceService _presence = new(new FakeProcessMonitor(), NullLogger<GamePresenceService>.Instance);
    private AppSettings _settings = new() { BoostEnabled = true, BoostWifiScanPaused = true };

    private WifiScanService Create() => new(_wifi, _presence, () => _settings.BoostEffective(), NullLogger<WifiScanService>.Instance);

    private void GameOnScreen() => _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);

    private void GameLeaves()
    {
        // The presence service debounces an exit over several polls.
        for (var i = 0; i < 5 && _presence.Current == GamePresence.InGame; i++)
        {
            _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        }
    }

    [Fact]
    public async Task NothingIsAskedOfWindowsWithoutTheGame()
    {
        using var service = Create();

        await service.SyncAsync();

        Assert.Equal(0, _wifi.Holds);
        Assert.Equal(0, _wifi.Releases);
        Assert.False(service.Status.Held);
    }

    [Fact]
    public async Task WithTheGameOnScreen_EveryConnectedInterfaceIsHeld_AndTheStatusSaysWhatWindowsAccepted()
    {
        _wifi.Interfaces = [new("Wi-Fi 6E", true, false), new("USB Wi-Fi", false, true)];
        GameOnScreen();
        using var service = Create();

        await service.SyncAsync();

        Assert.True(_wifi.Open);
        Assert.True(service.Status is { Enabled: true, GameOnScreen: true, HasWifi: true, Held: true });
        Assert.Equal(_wifi.Interfaces, service.Status.Interfaces);
    }

    [Fact]
    public async Task EachPassAsksAgain_SoAnInterfaceThatReconnectedIsHeldAgain()
    {
        GameOnScreen();
        using var service = Create();
        await service.SyncAsync();

        _wifi.Interfaces = [];
        await service.SyncAsync();
        _wifi.Interfaces = [new("Wi-Fi 6E", true, true)];
        await service.SyncAsync();

        Assert.Equal(3, _wifi.Holds);
        Assert.True(service.Status.Held);
    }

    [Fact]
    public async Task WhenTheGameLeaves_TheSessionIsClosed()
    {
        GameOnScreen();
        using var service = Create();
        await service.SyncAsync();

        GameLeaves();
        await service.SyncAsync();
        await service.SyncAsync();

        Assert.False(_wifi.Open);
        // Closed once, not at every pass after.
        Assert.Equal(1, _wifi.Releases);
        Assert.False(service.Status.Held);
    }

    [Fact]
    public async Task SwitchedOffMidGame_TheSessionIsClosed()
    {
        GameOnScreen();
        using var service = Create();
        await service.SyncAsync();

        _settings = _settings with { BoostWifiScanPaused = false };
        await service.SyncAsync();

        Assert.False(_wifi.Open);
        Assert.False(service.Status.Enabled);
    }

    [Fact]
    public async Task BoostOff_KeepsTheSwitchFromDoingAnything()
    {
        _settings = _settings with { BoostEnabled = false };
        GameOnScreen();
        using var service = Create();

        await service.SyncAsync();

        Assert.Equal(0, _wifi.Holds);
        Assert.False(service.Status.Enabled);
    }

    [Fact]
    public async Task APCWithoutWifi_SaysSo_AndHoldsNothing()
    {
        _wifi.Interfaces = null;
        GameOnScreen();
        using var service = Create();

        await service.SyncAsync();

        Assert.False(service.Status.HasWifi);
        Assert.Empty(service.Status.Interfaces);
        Assert.False(service.Status.Held);
    }

    [Fact]
    public async Task NotConnectedOverWifi_HoldsNothing_ButStillHasWifi()
    {
        _wifi.Interfaces = [];
        GameOnScreen();
        using var service = Create();

        await service.SyncAsync();

        Assert.True(service.Status.HasWifi);
        Assert.Empty(service.Status.Interfaces);
        Assert.False(service.Status.Held);
        Assert.False(service.Status.Failed);
    }

    [Fact]
    public async Task AnInterfaceWhereWindowsRefusedBoth_DoesNotCountAsHeld()
    {
        _wifi.Interfaces = [new("Wi-Fi", false, false)];
        GameOnScreen();
        using var service = Create();

        await service.SyncAsync();

        Assert.Single(service.Status.Interfaces);
        Assert.False(service.Status.Held);
    }

    [Fact]
    public async Task AFailedHold_IsNotHeld_AndTheSessionItMayHaveOpenedIsStillClosedAfterwards()
    {
        _wifi.Fails = new System.ComponentModel.Win32Exception(5);
        GameOnScreen();
        using var service = Create();
        await service.SyncAsync();

        Assert.False(service.Status.Held);
        Assert.True(service.Status.Failed);

        GameLeaves();
        await service.SyncAsync();

        Assert.Equal(1, _wifi.Releases);
    }

    [Fact]
    public async Task ClosingOptima_ClosesTheSession_AndNoPassAfterOpensAnother()
    {
        GameOnScreen();
        var service = Create();
        await service.SyncAsync();

        service.Dispose();
        await service.SyncAsync();

        Assert.False(_wifi.Open);
        Assert.Equal(1, _wifi.Holds);
    }

    [Fact]
    public void TheMasterSwitchOff_TurnsTheWifiPauseOff()
    {
        Assert.False(new AppSettings { BoostWifiScanPaused = true }.BoostEffective().BoostWifiScanPaused);
        Assert.True(new AppSettings { BoostEnabled = true, BoostWifiScanPaused = true }.BoostEffective().BoostWifiScanPaused);
    }

    [Fact]
    public void TheRealWlanServiceCanBeListed_WithoutChangingAnything()
    {
        // Opens a session and lists the connected interfaces, and sets nothing. On a PC without
        // Wi-Fi there is no session to open.
        using var session = WlanNative.Open();
        if (session is null)
        {
            return;
        }
        var connected = WlanNative.ConnectedInterfaces(session);

        Assert.All(connected, i =>
        {
            Assert.NotEqual(Guid.Empty, i.Id);
            Assert.False(string.IsNullOrWhiteSpace(i.Name));
            Assert.DoesNotContain('\0', i.Name);
        });
    }
}
