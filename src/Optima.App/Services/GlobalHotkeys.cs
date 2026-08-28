using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace Optima.App.Services;

/// <summary>
/// System-wide hotkeys via RegisterHotKey, so they fire even while the game window has focus.
/// The WPF InputBindings on MainWindow only work while Optima itself is the focused window,
/// which is exactly when a console or kill switch is least needed.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF9 = 0x78;
    private const uint VkF10 = 0x79;
    private const uint VkK = 0x4B;

    private const int ConsoleId = 0xB001;
    private const int KillGameId = 0xB002;
    private const int OverlayId = 0xB003;

    private readonly IntPtr _handle;
    private HwndSource? _source;

    /// <summary>Alt+F9: toggle the floating log console.</summary>
    public event Action? ConsoleRequested;

    /// <summary>Ctrl+Alt+K: kill the game process tree.</summary>
    public event Action? KillGameRequested;

    /// <summary>Alt+F10: toggle the in-game FPS overlay.</summary>
    public event Action? OverlayRequested;

    public GlobalHotkeys(Window window)
    {
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(OnWindowMessage);

        RegisterOrWarn(ConsoleId, ModAlt | ModNoRepeat, VkF9, "Alt+F9");
        RegisterOrWarn(KillGameId, ModControl | ModAlt | ModNoRepeat, VkK, "Ctrl+Alt+K");
        RegisterOrWarn(OverlayId, ModAlt | ModNoRepeat, VkF10, "Alt+F10");
    }

    private void RegisterOrWarn(int id, uint modifiers, uint key, string label)
    {
        // A taken hotkey must never block startup; the in-app buttons still work.
        if (!RegisterHotKey(_handle, id, modifiers, key))
        {
            Log.Warning("Global hotkey {Hotkey} is already in use by another application", label);
        }
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            switch (wParam.ToInt32())
            {
                case ConsoleId:
                    ConsoleRequested?.Invoke();
                    handled = true;
                    break;
                case KillGameId:
                    KillGameRequested?.Invoke();
                    handled = true;
                    break;
                case OverlayId:
                    OverlayRequested?.Invoke();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_handle, ConsoleId);
        UnregisterHotKey(_handle, KillGameId);
        UnregisterHotKey(_handle, OverlayId);
        _source?.RemoveHook(OnWindowMessage);
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
