using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.App.Services;
using Optima.Core.Configuration;
using Optima.Monitoring.Metrics;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>
/// COMP page: gear checks for competitive play. Network (ad-hoc ping + wifi link),
/// input (raw-input mouse meter, key timing, display scale) and thermals (streamed
/// through the elevated helper). Every readout carries its honest precision limit.
/// </summary>
public sealed partial class CompViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly HardwareStreamClient _hardware;
    private readonly ILogger<CompViewModel> _logger;
    private readonly Stopwatch _keyClock = Stopwatch.StartNew();
    private readonly List<double> _keyIntervals = [];
    private double _lastKeyDownMs;

    public RawMouseMeter MouseMeter { get; } = new();

    public CompViewModel(
        SettingsService settings,
        HardwareStreamClient hardware,
        ILogger<CompViewModel> logger)
    {
        _settings = settings;
        _hardware = hardware;
        _logger = logger;
        _hardware.SampleReceived += OnHardwareSample;
    }

    // ---- Network ----
    [ObservableProperty] private string _pingTarget = "1.1.1.1";
    [ObservableProperty] private string _pingResult = string.Empty;
    [ObservableProperty] private bool _pingBusy;
    [ObservableProperty] private string _wifiResult = "not checked yet";

    // ---- Input ----
    [ObservableProperty] private bool _mouseMeterActive;
    [ObservableProperty] private string _mouseCounts = "0";
    [ObservableProperty] private string _mousePollingRate = "move the mouse...";
    [ObservableProperty] private string _dpiDistance = "10";
    [ObservableProperty] private string _dpiResult = string.Empty;
    [ObservableProperty] private string _keyTestText = string.Empty;
    [ObservableProperty] private string _keyIntervalResult = "press keys in the box...";
    [ObservableProperty] private string _displayScaleText = "---";

    // ---- Thermals ----
    [ObservableProperty] private bool _thermalsActive;
    [ObservableProperty] private string _cpuThermalText = "---";
    [ObservableProperty] private string _gpuThermalText = "---";
    [ObservableProperty] private string _thermalStatus = string.Empty;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        PingTarget = (await _settings.GetSettingsAsync(ct)).NetworkReferenceHost;
    }

    [RelayCommand]
    private async Task RunPingTestAsync()
    {
        if (PingBusy)
        {
            return;
        }
        PingBusy = true;
        PingResult = "pinging...";
        try
        {
            using var ping = new Ping();
            var times = new List<double>();
            var lost = 0;
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    var reply = await ping.SendPingAsync(PingTarget.Trim(), 1500);
                    if (reply.Status == IPStatus.Success)
                    {
                        times.Add(reply.RoundtripTime);
                    }
                    else
                    {
                        lost++;
                    }
                }
                catch (PingException)
                {
                    lost++;
                }
                await Task.Delay(250);
            }

            if (times.Count == 0)
            {
                PingResult = $"no replies from {PingTarget} ({lost}/10 lost)";
            }
            else
            {
                var jitter = times.Count > 1
                    ? times.Zip(times.Skip(1), (a, b) => Math.Abs(b - a)).Average()
                    : 0;
                PingResult =
                    $"{times.Average():F0} ms avg · {jitter:F1} ms jitter · {lost * 10}% loss · {times.Min():F0}-{times.Max():F0} ms range";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ping test failed");
            PingResult = "ping test failed: " + ex.Message;
        }
        finally
        {
            PingBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshWifiAsync()
    {
        WifiResult = "checking...";
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            string? Grab(string label)
            {
                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                    {
                        var colon = trimmed.IndexOf(':');
                        if (colon > 0)
                        {
                            return trimmed[(colon + 1)..].Trim();
                        }
                    }
                }
                return null;
            }

            var ssid = Grab("SSID");
            if (ssid is null || ssid.Length == 0)
            {
                WifiResult = "no wireless interface connected (wired connections do not appear here)";
                return;
            }
            var signal = Grab("Signal") ?? "?";
            var radio = Grab("Radio type") ?? "?";
            var rx = Grab("Receive rate (Mbps)") ?? "?";
            var tx = Grab("Transmit rate (Mbps)") ?? "?";
            WifiResult = $"{ssid} · signal {signal} · {radio} · {rx}/{tx} Mbps";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wifi readout failed");
            WifiResult = "wifi readout failed: " + ex.Message;
        }
    }

    // ---- Input ----

    [RelayCommand]
    private void ToggleMouseMeter()
    {
        if (MouseMeterActive)
        {
            MouseMeter.Stop();
            MouseMeterActive = false;
            return;
        }
        if (Application.Current?.MainWindow is { } window)
        {
            MouseMeter.Start(window);
            MouseMeterActive = MouseMeter.Active;
            if (!MouseMeterActive)
            {
                MousePollingRate = "raw input registration failed";
            }
        }
    }

    /// <summary>Called by the view's dispatcher timer while the meter runs.</summary>
    public void RefreshMouseReadout()
    {
        if (!MouseMeterActive)
        {
            return;
        }
        MouseCounts = MouseMeter.Counts.ToString(CultureInfo.InvariantCulture);
        MousePollingRate = MouseMeter.PollingRateHz > 0
            ? $"{MouseMeter.PollingRateHz:F0} Hz"
            : "move the mouse...";
    }

    [RelayCommand]
    private void ResetMouseCounts()
    {
        MouseMeter.Reset();
        MouseCounts = "0";
        DpiResult = string.Empty;
    }

    [RelayCommand]
    private void ComputeDpi()
    {
        if (!double.TryParse(DpiDistance.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var cm) || cm <= 0)
        {
            DpiResult = "enter the distance you moved, in centimeters";
            return;
        }
        var counts = MouseMeter.Counts;
        if (counts < 100)
        {
            DpiResult = "move the mouse a longer straight line first (left to right)";
            return;
        }
        var inches = cm / 2.54;
        DpiResult = $"about {counts / inches:F0} DPI ({counts} counts over {cm:F1} cm)";
    }

    public void OnTestKeyDown()
    {
        var now = _keyClock.Elapsed.TotalMilliseconds;
        if (_lastKeyDownMs > 0)
        {
            var interval = now - _lastKeyDownMs;
            if (interval < 2000)
            {
                _keyIntervals.Add(interval);
                if (_keyIntervals.Count > 50)
                {
                    _keyIntervals.RemoveAt(0);
                }
                KeyIntervalResult =
                    $"last {interval:F0} ms · min {_keyIntervals.Min():F0} ms · " +
                    $"hold a key: repeat about {1000 / Math.Max(1, _keyIntervals.TakeLast(5).Average()):F0} keys/s";
            }
        }
        _lastKeyDownMs = now;
    }

    // ---- Thermals ----

    [RelayCommand]
    private async Task ToggleThermalsAsync()
    {
        if (ThermalsActive)
        {
            await _hardware.StopAsync();
            ThermalsActive = false;
            ThermalStatus = string.Empty;
            CpuThermalText = GpuThermalText = "---";
            return;
        }
        ThermalStatus = "starting (administrator prompt may appear)...";
        var started = await _hardware.StartAsync();
        ThermalsActive = started;
        ThermalStatus = started
            ? "streaming from the hardware sensors"
            : "not started (administrator declined, or sensor access failed)";
    }

    private void OnHardwareSample(HardwareSample sample)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            CpuThermalText = Format(sample.CpuTempC, sample.CpuLoadPct);
            GpuThermalText = Format(sample.GpuTempC, sample.GpuLoadPct);
        });

        static string Format(double? temp, double? load)
        {
            var t = temp is { } tv ? $"{tv:F0}°C" : "n/a";
            var l = load is { } lv ? $" · {lv:F0}% load" : "";
            return t + l;
        }
    }

    public void SetDisplayScale(double dpiScale)
        => DisplayScaleText = $"{dpiScale * 100:F0}% ({dpiScale * 96:F0} DPI)";
}
