using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Optima.Core.Abstractions;
using Optima.Core.Boost;
using Optima.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>
/// Holds a system timer resolution on behalf of Optima's own process. No administrator rights are
/// involved. The half-millisecond step is only reachable through NtSetTimerResolution; the documented
/// timeBeginPeriod stops at one millisecond.
/// </summary>
public sealed class WindowsTimerResolution : ITimerResolution
{
    private const string KernelKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const string GlobalRequestsValue = "GlobalTimerResolutionRequests";

    private readonly ILogger<WindowsTimerResolution> _logger;
    private readonly object _gate = new();
    private uint _held;

    public WindowsTimerResolution(ILogger<WindowsTimerResolution> logger)
    {
        _logger = logger;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desiredResolution, [MarshalAs(UnmanagedType.U1)] bool setResolution, out uint currentResolution);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimumResolution, out uint maximumResolution, out uint currentResolution);

    public int WindowsBuild => Environment.OSVersion.Version.Build;

    public bool GlobalRequestsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(KernelKey);
                return key?.GetValue(GlobalRequestsValue) is int value && value == 1;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    public double CurrentMs
        => NtQueryTimerResolution(out _, out _, out var current) == 0 ? current / 10_000.0 : 0;

    public double? Hold(uint hundredNanoseconds)
    {
        lock (_gate)
        {
            if (_held != 0 && _held != hundredNanoseconds)
            {
                ReleaseCore();
            }

            // Windows 11 would drop the request as soon as Optima's window is hidden behind the game.
            if (TimerResolutionPolicy.ReachFor(WindowsBuild) == TimerResolutionReach.OwnProcessUnlessGlobalSwitch)
            {
                using var self = Process.GetCurrentProcess();
                TryHonor(self);
            }

            var status = NtSetTimerResolution(hundredNanoseconds, true, out var applied);
            if (status != 0)
            {
                _logger.LogWarning("Windows refused timer resolution {Requested} (0x{Status:X8})", hundredNanoseconds, status);
                return null;
            }
            _held = hundredNanoseconds;
            _logger.LogInformation("Timer resolution held: asked {Asked} ms, Windows applied {Applied} ms",
                hundredNanoseconds / 10_000.0, applied / 10_000.0);
            return applied / 10_000.0;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            ReleaseCore();
        }
    }

    private void ReleaseCore()
    {
        if (_held == 0)
        {
            return;
        }
        NtSetTimerResolution(_held, false, out _);
        _held = 0;
        _logger.LogInformation("Timer resolution released");
    }

    public void HonorRequestsOf(int processId)
    {
        if (TimerResolutionPolicy.ReachFor(WindowsBuild) != TimerResolutionReach.OwnProcessUnlessGlobalSwitch)
        {
            return;
        }
        try
        {
            using var process = Process.GetProcessById(processId);
            TryHonor(process);
        }
        catch (ArgumentException)
        {
            // Exited in the meantime.
        }
    }

    private void TryHonor(Process process)
    {
        try
        {
            ProcessNative.HonorTimerResolutionRequests(process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Could not set the timer rule on pid {Pid}", process.Id);
        }
    }
}
