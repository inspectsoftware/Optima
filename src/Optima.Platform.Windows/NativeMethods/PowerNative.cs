using System.Runtime.InteropServices;

namespace Optima.Platform.Windows.NativeMethods;

/// <summary>Documented power scheme APIs (powrprof.dll).</summary>
internal static class PowerNative
{
    internal static readonly Guid BalancedScheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    internal static readonly Guid HighPerformanceScheme = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    internal static readonly Guid UltimatePerformanceScheme = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    private const int ERROR_SUCCESS = 0;

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDuplicateScheme(IntPtr rootPowerKey, ref Guid sourceSchemeGuid, out IntPtr destinationSchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(
        IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        uint accessFlags, uint index, [Out] byte[]? buffer, ref uint bufferSize);

    private const uint ACCESS_SCHEME = 16;

    internal static Guid GetActiveScheme()
    {
        var result = PowerGetActiveScheme(IntPtr.Zero, out var ptr);
        if (result != ERROR_SUCCESS)
        {
            throw new System.ComponentModel.Win32Exception((int)result, "PowerGetActiveScheme failed");
        }
        try
        {
            return Marshal.PtrToStructure<Guid>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    internal static void SetActiveScheme(Guid scheme)
    {
        var result = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (result != ERROR_SUCCESS)
        {
            throw new System.ComponentModel.Win32Exception((int)result, $"PowerSetActiveScheme({scheme}) failed");
        }
    }

    internal static string GetFriendlyName(Guid scheme)
    {
        uint size = 0;
        _ = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0)
        {
            return scheme.ToString();
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var result = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size);
            return result == ERROR_SUCCESS ? (Marshal.PtrToStringUni(buffer) ?? scheme.ToString()) : scheme.ToString();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static IReadOnlyList<Guid> EnumerateSchemes()
    {
        var schemes = new List<Guid>();
        for (uint index = 0; ; index++)
        {
            var buffer = new byte[16];
            var size = (uint)buffer.Length;
            var result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, index, buffer, ref size);
            if (result != ERROR_SUCCESS)
            {
                break;
            }
            schemes.Add(new Guid(buffer));
        }
        return schemes;
    }

    /// <summary>Creates a duplicate of the Ultimate Performance template when it is not yet listed.</summary>
    internal static Guid EnsureUltimatePerformance()
    {
        var existing = EnumerateSchemes();
        if (existing.Contains(UltimatePerformanceScheme))
        {
            return UltimatePerformanceScheme;
        }

        var source = UltimatePerformanceScheme;
        var result = PowerDuplicateScheme(IntPtr.Zero, ref source, out var destPtr);
        if (result != ERROR_SUCCESS)
        {
            // Template not available (e.g. Home SKU restrictions), so fall back to High Performance.
            return HighPerformanceScheme;
        }
        try
        {
            return Marshal.PtrToStructure<Guid>(destPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(destPtr);
        }
    }
}
