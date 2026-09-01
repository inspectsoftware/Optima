using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Optima.Core.Configuration;
using Optima.Core.Launch;
using Optima.Core.Models;
using Serilog;

namespace Optima.App.Services;

/// <summary>
/// Notification-area icon for Optima. Always present while the app runs; left or
/// right click opens a small menu (Show / Terminate Process / Logs / Performance).
/// During a game session the main window hides to the tray once the game is running
/// and comes back when the session ends, per <see cref="TrayVisibilityPolicy"/>.
/// </summary>
public sealed class TrayService : IDisposable
{
    // WM_APP range so the callback can never collide with a system message.
    private const int WmTrayCallback = 0x8001;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const uint TrayIconId = 1;

    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;

    // Explorer broadcasts this when the taskbar is (re)created; the icon must be re-added.
    private static readonly int WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly Window _window;
    private readonly IntPtr _handle;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly ContextMenu _menu;
    private readonly TrayVisibilityPolicy _policy = new();
    private readonly SettingsService _settings;
    private readonly AppShutdown _shutdown;
    private readonly MenuItem _watchModeItem;
    private HwndSource? _source;
    private LaunchOrchestrator? _orchestrator;
    private bool _keepInTrayOnClose;
    private bool _watchModeEnabled;

    /// <summary>Tray "Terminate Process": kill the game, same route as the kill buttons.</summary>
    public event Action? TerminateGameRequested;

    /// <summary>Tray "Logs"/"Performance": show the window and open the given page.</summary>
    public event Action<string>? NavigateRequested;

    public TrayService(Window window, SettingsService settings, AppShutdown shutdown)
    {
        _window = window;
        _settings = settings;
        _shutdown = shutdown;
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(OnWindowMessage);

        (_icon, _ownsIcon) = LoadTrayIcon();

        _menu = new ContextMenu();
        AddMenuItem("SHOW", ShowMainWindow);
        AddMenuItem("TERMINATE PROCESS", () => TerminateGameRequested?.Invoke());
        _watchModeItem = AddMenuItem("WATCHDOG: OFF", () => _ = ToggleWatchModeAsync());
        AddMenuItem("LOGS", () => NavigateRequested?.Invoke("LOGS"));
        AddMenuItem("PERFORMANCE", () => NavigateRequested?.Invoke("PERFORMANCE"));
        var separator = new Separator();
        separator.SetResourceReference(FrameworkElement.StyleProperty, typeof(Separator));
        _menu.Items.Add(separator);
        AddMenuItem("EXIT", _shutdown.RequestExit);

        _window.Closing += OnWindowClosing;
        _settings.SettingsChanged += OnSettingsChanged;
        _ = LoadCloseBehaviorAsync();

        AddIcon();
    }

    private async Task LoadCloseBehaviorAsync()
    {
        try
        {
            var settings = await _settings.GetSettingsAsync();
            _keepInTrayOnClose = settings.KeepInTrayOnClose;
            UpdateWatchModeItem(settings.EnableWatchMode);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read settings for the tray close behavior");
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        _keepInTrayOnClose = settings.KeepInTrayOnClose;
        _window.Dispatcher.BeginInvoke(() => UpdateWatchModeItem(settings.EnableWatchMode));
    }

    private void UpdateWatchModeItem(bool enabled)
    {
        _watchModeEnabled = enabled;
        _watchModeItem.Header = enabled ? "WATCHDOG: ON" : "WATCHDOG: OFF";
    }

    private async Task ToggleWatchModeAsync()
    {
        try
        {
            await _settings.UpdateSettingsAsync(s => s with { EnableWatchMode = !_watchModeEnabled });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not toggle watch mode from the tray");
        }
    }

    /// <summary>
    /// The window's close button never quits on its own. With "keep in tray" enabled it hides
    /// the window; otherwise the close is handed to <see cref="AppShutdown"/>, which asks about
    /// the installed driver first and then shuts the application down.
    /// </summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdown.IsShuttingDown)
        {
            return;
        }
        e.Cancel = true;
        if (_keepInTrayOnClose)
        {
            _window.Hide();
        }
        else
        {
            _shutdown.RequestExit();
        }
    }

    /// <summary>Hide the window while the game runs; bring it back when the session ends.</summary>
    public void AttachOrchestrator(LaunchOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        orchestrator.ProgressChanged += OnLaunchProgress;
    }

    public void ShowMainWindow()
    {
        _policy.OnManualShow();
        RestoreWindow();
    }

    private void RestoreWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
        PlayReturn();
    }

    private void OnLaunchProgress(object? sender, LaunchProgress progress)
    {
        // Orchestrator events arrive on a background thread.
        _window.Dispatcher.BeginInvoke(() =>
        {
            switch (_policy.OnPhase(progress.Phase))
            {
                case TrayWindowAction.Hide:
                    HideToTray();
                    break;
                case TrayWindowAction.Restore:
                    RestoreWindow();
                    break;
            }
        });
    }

    // ---- launch choreography ----------------------------------------------------------
    // The window slips toward the tray corner and dissolves when the game is confirmed
    // running; the reverse plays when the session ends. Instant under reduced motion.

    private static readonly TimeSpan Slip = TimeSpan.FromMilliseconds(500);

    private void HideToTray()
    {
        if (_window.Content is not FrameworkElement content || !Motion.Enabled)
        {
            _window.Hide();
            return;
        }
        var scale = new System.Windows.Media.ScaleTransform(1, 1);
        content.RenderTransformOrigin = new Point(1, 1);
        content.RenderTransform = scale;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };
        var shrink = new System.Windows.Media.Animation.DoubleAnimation(1, 0.85, Slip) { EasingFunction = ease };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, Slip) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            _window.Hide();
            content.BeginAnimation(UIElement.OpacityProperty, null);
            content.Opacity = 1;
            content.RenderTransform = null;
        };
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, shrink);
        content.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void PlayReturn()
    {
        if (_window.Content is not FrameworkElement content || !Motion.Enabled)
        {
            return;
        }
        var scale = new System.Windows.Media.ScaleTransform(0.85, 0.85);
        content.RenderTransformOrigin = new Point(1, 1);
        content.RenderTransform = scale;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var grow = new System.Windows.Media.Animation.DoubleAnimation(0.85, 1, Slip) { EasingFunction = ease };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, Slip) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            content.BeginAnimation(UIElement.OpacityProperty, null);
            content.Opacity = 1;
            content.RenderTransform = null;
        };
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
        content.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private MenuItem AddMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
        return item;
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmTrayCallback && wParam.ToInt64() == TrayIconId)
        {
            var mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMessage is WmLButtonUp or WmRButtonUp)
            {
                OpenMenu();
                handled = true;
            }
        }
        else if (msg == WmTaskbarCreated)
        {
            AddIcon();
        }
        return IntPtr.Zero;
    }

    private void OpenMenu()
    {
        // Classic tray fix: without foregrounding our window first, the popup
        // would not close when the user clicks elsewhere or presses Esc.
        SetForegroundWindow(_handle);
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private void AddIcon()
    {
        var data = NewIconData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = WmTrayCallback;
        data.hIcon = _icon;
        data.szTip = "Optima";
        if (!Shell_NotifyIconW(NimAdd, ref data))
        {
            Log.Warning("Could not add the tray icon");
        }
    }

    private NotifyIconData NewIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _handle,
        uID = TrayIconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    /// <summary>The tray icon is the app icon, read straight from the exe.</summary>
    private static (IntPtr Icon, bool Owned) LoadTrayIcon()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null && ExtractIconExW(exe, 0, out var large, out var small, 1) > 0)
        {
            if (small != IntPtr.Zero && large != IntPtr.Zero)
            {
                DestroyIcon(large);
            }
            var icon = small != IntPtr.Zero ? small : large;
            if (icon != IntPtr.Zero)
            {
                return (icon, true);
            }
        }
        Log.Warning("Could not load the app icon for the tray; using the stock icon");
        return (LoadIcon(IntPtr.Zero, IdiApplication), false);
    }

    public void Dispose()
    {
        _window.Closing -= OnWindowClosing;
        _settings.SettingsChanged -= OnSettingsChanged;
        if (_orchestrator is not null)
        {
            _orchestrator.ProgressChanged -= OnLaunchProgress;
            _orchestrator = null;
        }
        var data = NewIconData();
        Shell_NotifyIconW(NimDelete, ref data);
        _source?.RemoveHook(OnWindowMessage);
        _source = null;
        if (_ownsIcon)
        {
            DestroyIcon(_icon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private static readonly IntPtr IdiApplication = new(0x7F00);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex,
        out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
