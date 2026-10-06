using System.Runtime.InteropServices;

namespace Optima.Platform.Windows.NativeMethods;

/// <summary>Documented power scheme APIs (powrprof.dll).</summary>
internal static class PowerNative
{
    internal static readonly Guid BalancedScheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    internal static readonly Guid HighPerformanceScheme = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    internal static readonly Guid UltimatePerformanceScheme = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    /// <summary>The one copy of Ultimate Performance Optima makes where Windows hides the built-in plan.</summary>
    internal static readonly Guid OptimaUltimatePerformanceScheme = new("0b71a3c5-6d2e-4f0a-9c1b-5e8f2a7d4c10");

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
    private static extern uint PowerDuplicateScheme(IntPtr rootPowerKey, ref Guid sourceSchemeGuid, ref IntPtr destinationSchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(
        IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        uint accessFlags, uint index, [Out] byte[]? buffer, ref uint bufferSize);

    private const uint ACCESS_SCHEME = 16;

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);

    // Processor power management, "processor performance core parking min cores" (percent kept unparked).
    private static readonly Guid ProcessorSubGroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid MinUnparkedCores = new("0cc5b647-c1df-4637-891a-dec35c318583");

    internal static (uint Ac, uint Dc) ReadMinUnparkedCores(Guid scheme)
    {
        var sub = ProcessorSubGroup;
        var setting = MinUnparkedCores;
        var acResult = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var ac);
        var dcResult = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var dc);
        if (acResult != ERROR_SUCCESS || dcResult != ERROR_SUCCESS)
        {
            throw new System.ComponentModel.Win32Exception((int)(acResult != ERROR_SUCCESS ? acResult : dcResult), "Reading the core parking setting failed");
        }
        return (ac, dc);
    }

    internal static void WriteMinUnparkedCores(Guid scheme, uint ac, uint dc)
    {
        var sub = ProcessorSubGroup;
        var setting = MinUnparkedCores;
        var acResult = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, ac);
        var dcResult = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, dc);
        if (acResult != ERROR_SUCCESS || dcResult != ERROR_SUCCESS)
        {
            throw new System.ComponentModel.Win32Exception((int)(acResult != ERROR_SUCCESS ? acResult : dcResult), "Writing the core parking setting failed");
        }
    }

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

    internal static Guid EnsureUltimatePerformance()
    {
        var existing = EnumerateSchemes();
        if (existing.Contains(UltimatePerformanceScheme))
        {
            return UltimatePerformanceScheme;
        }
        if (existing.Contains(OptimaUltimatePerformanceScheme))
        {
            return OptimaUltimatePerformanceScheme;
        }

        // Duplicated under a GUID of Optima's own: left to pick one, Windows makes a new plan with a
        // random GUID on every call, and nothing here could find it again the next time.
        var source = UltimatePerformanceScheme;
        var destPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(OptimaUltimatePerformanceScheme, destPtr, fDeleteOld: false);
            var result = PowerDuplicateScheme(IntPtr.Zero, ref source, ref destPtr);
            return result == ERROR_SUCCESS ? OptimaUltimatePerformanceScheme : HighPerformanceScheme;
        }
        finally
        {
            Marshal.FreeHGlobal(destPtr);
        }
    }
}
