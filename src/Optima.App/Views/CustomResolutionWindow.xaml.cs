using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Optima.App.ViewModels;
using Optima.Core.Abstractions;
using Optima.Core.Launch;
using Optima.Core.Models;
using Optima.Driver.Providers;

namespace Optima.App.Views;

/// <summary>
/// Sets a real monitor's resolution, with no virtual display involved. Every change is temporary
/// (<see cref="IDisplayService.ApplyModeAsync"/> never writes the registry), so the mode Windows has
/// saved is always the one to go back to and this window keeps no state of its own.
/// </summary>
public partial class CustomResolutionWindow : Window
{
    private const int KeepSeconds = 15;

    private sealed record Monitor(DisplayInfo Info)
    {
        public override string ToString()
            => $"{Info.FriendlyName}{(Info.IsPrimary ? " (main)" : string.Empty)} · {Info.CurrentMode}";
    }

    private readonly IDisplayService _displays;
    private readonly Func<bool> _gameIsRunning;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _keepTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<DisplayMode> _modes = [];
    private DisplayMode? _saved;
    private DisplayMode? _goBackTo;
    private Monitor? _goBackOn;
    private int _keepLeft;
    private bool _working;

    public CustomResolutionWindow(IDisplayService displays, Func<bool> gameIsRunning, ILogger logger)
    {
        InitializeComponent();
        _displays = displays;
        _gameIsRunning = gameIsRunning;
        _logger = logger;
        _keepTimer.Tick += OnKeepTick;
        Loaded += async (_, _) => await LoadMonitorsAsync(null);
    }

    private Monitor? Selected => MonitorCombo.SelectedItem as Monitor;

    private async Task LoadMonitorsAsync(string? keepDevice)
    {
        try
        {
            var monitors = (await _displays.GetDisplaysAsync())
                .Where(d => d.IsActive && d.HasMonitor
                    && !d.AdapterName.Contains(MttVddProvider.DeviceNameMarker, StringComparison.OrdinalIgnoreCase))
                .Select(d => new Monitor(d))
                .ToList();
            MonitorCombo.ItemsSource = monitors;
            MonitorCombo.SelectedItem = monitors.FirstOrDefault(m => m.Info.DeviceName == keepDevice)
                ?? monitors.FirstOrDefault(m => m.Info.IsPrimary)
                ?? monitors.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The monitors could not be read for the custom resolution window");
            Say("Could not read your monitors. See the log on the Debug page.");
        }
    }

    private async void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } monitor)
        {
            UpdateForm();
            return;
        }
        try
        {
            var device = monitor.Info.DeviceName;
            var modes = await _displays.GetSupportedModesAsync(device);
            var saved = await _displays.GetSavedModeAsync(device);
            if (Selected != monitor)
            {
                return;
            }
            _modes = modes;
            _saved = saved;
            if (WidthBox.Text.Length == 0 && HeightBox.Text.Length == 0)
            {
                WidthBox.Text = monitor.Info.CurrentMode.Width.ToString();
                HeightBox.Text = monitor.Info.CurrentMode.Height.ToString();
            }
            UpdateForm();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The modes of {Device} could not be read", monitor.Info.DeviceName);
            Say("Could not read what this monitor offers. See the log on the Debug page.");
        }
    }

    private void OnSizeTyped(object sender, TextChangedEventArgs e) => UpdateForm();

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        var size = ((string)((Button)sender).Tag).Split(',');
        WidthBox.Text = size[0];
        HeightBox.Text = size[1];
    }

    /// <summary>Everything that follows from the monitor and the typed size, said before Apply is pressed.</summary>
    private void UpdateForm()
    {
        var current = Selected?.Info.CurrentMode ?? default;
        var valid = CustomResolution.TryParse(WidthBox.Text, HeightBox.Text, out var width, out var height);

        var rate = RateCombo.SelectedItem as int?;
        var rates = valid ? CustomResolution.RatesFor(_modes, width, height, current.RefreshRate) : [];
        RateCombo.ItemsSource = rates;
        RateCombo.SelectedItem = rate is { } kept && rates.Contains(kept) ? kept : rates.FirstOrDefault();

        ListedText.Text = !valid
            ? "Enter a size of at least 640 x 480."
            : CustomResolution.IsListed(_modes, width, height)
                ? "On this monitor's list."
                : "Not on this monitor's list. Windows will refuse it until you add it in your graphics card's settings.";

        var idle = !_working && _goBackTo is null;
        Form.IsEnabled = idle;
        ApplyButton.IsEnabled = idle && valid && Selected is not null;
        RestoreButton.IsEnabled = idle && Selected is not null && CustomResolution.CanRestore(current, _saved);
        RestoreButton.Content = _saved is { } saved ? $"Restore {saved}" : "Restore";
    }

    private void Say(string message)
    {
        MessageText.Text = message;
        MessagePanel.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool GameBlocks()
    {
        if (!_gameIsRunning())
        {
            return false;
        }
        Say("Not while Critical Ops is running: this switches your screens around. Close the game first.");
        return true;
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } monitor || GameBlocks()
            || RateCombo.SelectedItem is not int rate
            || !CustomResolution.TryParse(WidthBox.Text, HeightBox.Text, out var width, out var height))
        {
            return;
        }

        var mode = new DisplayMode(width, height, rate);
        var previous = monitor.Info.CurrentMode;
        if (mode == previous)
        {
            Say($"This monitor already runs at {mode}.");
            return;
        }

        Say(string.Empty);
        if (await ChangeModeAsync(monitor, mode))
        {
            _goBackTo = previous;
            _goBackOn = monitor;
            _keepLeft = KeepSeconds;
            ShowCountdown();
            KeepPanel.Visibility = Visibility.Visible;
            _keepTimer.Start();
            UpdateForm();
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } monitor || _saved is not { } saved || GameBlocks())
        {
            return;
        }
        Say(string.Empty);
        if (await ChangeModeAsync(monitor, saved))
        {
            Say($"Back to {saved}.");
        }
    }

    private void OnKeep(object sender, RoutedEventArgs e)
    {
        StopCountdown();
        Say("Kept. It lasts until you restore it here or restart the PC.");
        UpdateForm();
    }

    private async void OnGoBack(object sender, RoutedEventArgs e) => await GoBackAsync();

    private async void OnKeepTick(object? sender, EventArgs e)
    {
        if (--_keepLeft > 0)
        {
            ShowCountdown();
            return;
        }
        await GoBackAsync();
    }

    private void ShowCountdown()
        => KeepText.Text = $"Keep this resolution? Going back in {_keepLeft} second{(_keepLeft == 1 ? string.Empty : "s")}.";

    private void StopCountdown()
    {
        _keepTimer.Stop();
        _goBackTo = null;
        KeepPanel.Visibility = Visibility.Collapsed;
    }

    private async Task GoBackAsync()
    {
        if (_goBackTo is not { } previous || _goBackOn is not { } monitor)
        {
            return;
        }
        StopCountdown();
        if (await ChangeModeAsync(monitor, previous))
        {
            Say($"Back to {previous}.");
        }
    }

    /// <summary>A close during the countdown is not a yes: the change is taken back first.</summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_goBackTo is null)
        {
            _keepTimer.Stop();
            return;
        }
        e.Cancel = true;
        await GoBackAsync();
        Close();
    }

    /// <summary>Applies a mode and reads the monitors again. False, with the reason on screen, when it did not go through.</summary>
    private async Task<bool> ChangeModeAsync(Monitor monitor, DisplayMode mode)
    {
        _working = true;
        UpdateForm();
        try
        {
            await _displays.ApplyModeAsync(monitor.Info.DeviceName, mode);
            await LoadMonitorsAsync(monitor.Info.DeviceName);
            return true;
        }
        catch (OptimaException ex) when (ex.Error.Code == "DISPLAY_MODE_UNSUPPORTED")
        {
            Say($"Windows refused {mode} on this monitor because it is not on the monitor's list. Add it once as a custom resolution in NVIDIA Control Panel, AMD Software, Intel Graphics Command Center or Custom Resolution Utility (CRU), then press Apply again.");
        }
        catch (OptimaException ex)
        {
            Say(DisplayViewModel.Describe(ex.Error));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying {Mode} to {Device} failed", mode, monitor.Info.DeviceName);
            Say("That did not work. See the log on the Debug page for details.");
        }
        finally
        {
            _working = false;
            UpdateForm();
        }
        return false;
    }
}
