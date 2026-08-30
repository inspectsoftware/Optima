using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Optima.App.Services;

/// <summary>
/// Raw-input mouse metering for the COMP page. WM_INPUT delivers the device's own motion
/// packets, so the event rate approximates the mouse's polling rate and the deltas are
/// hardware counts (independent of Windows pointer speed), which is what makes the DPI
/// calculation honest. Purely observational; registration is removed on Stop.
/// </summary>
public sealed class RawMouseMeter : IDisposable
{
    private HwndSource? _source;
    private long _counts;
    private int _eventsThisWindow;
    private long _windowStartedTicks;
    private double _lastRateHz;

    public bool Active { get; private set; }

    /// <summary>Accumulated |dx| hardware counts since the last reset.</summary>
    public long Counts => Interlocked.Read(ref _counts);

    /// <summary>Event rate over the last completed one-second window while the mouse moves.</summary>
    public double PollingRateHz => _lastRateHz;

    public void Start(Window window)
    {
        if (Active)
        {
            return;
        }
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);

        var device = new RawInputDevice
        {
            UsagePage = 0x01, // generic desktop
            Usage = 0x02,     // mouse
            Flags = RidevInputSink,
            Target = handle,
        };
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            _source?.RemoveHook(WndProc);
            _source = null;
            return;
        }
        Reset();
        Active = true;
    }

    public void Stop()
    {
        if (!Active)
        {
            return;
        }
        var device = new RawInputDevice
        {
            UsagePage = 0x01,
            Usage = 0x02,
            Flags = RidevRemove,
            Target = IntPtr.Zero,
        };
        RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        _source?.RemoveHook(WndProc);
        _source = null;
        Active = false;
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _counts, 0);
        _eventsThisWindow = 0;
        _windowStartedTicks = Environment.TickCount64;
        _lastRateHz = 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmInput)
        {
            return IntPtr.Zero;
        }

        uint size = 0;
        GetRawInputData(lParam, RidInput, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RawInputHeader>());
        if (size == 0)
        {
            return IntPtr.Zero;
        }
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RidInput, buffer, ref size, (uint)Marshal.SizeOf<RawInputHeader>()) != size)
            {
                return IntPtr.Zero;
            }
            var input = Marshal.PtrToStructure<RawInput>(buffer);
            if (input.Header.Type != 0) // RIM_TYPEMOUSE
            {
                return IntPtr.Zero;
            }

            var dx = Math.Abs(input.Mouse.LastX);
            if (dx > 0 || Math.Abs(input.Mouse.LastY) > 0)
            {
                Interlocked.Add(ref _counts, dx);
                _eventsThisWindow++;
                var elapsed = Environment.TickCount64 - _windowStartedTicks;
                if (elapsed >= 1000)
                {
                    _lastRateHz = _eventsThisWindow * 1000.0 / elapsed;
                    _eventsThisWindow = 0;
                    _windowStartedTicks = Environment.TickCount64;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Stop();

    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouse
    {
        public ushort Flags;
        public ushort ButtonFlags;
        public ushort ButtonData;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInput
    {
        public RawInputHeader Header;
        public RawMouse Mouse;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] RawInputDevice[] devices,
        uint numDevices, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data,
        ref uint size, uint sizeHeader);
}
