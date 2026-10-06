using System.Runtime.InteropServices;
using Optima.Core.Launch;

namespace Optima.Platform.Windows.NativeMethods;

/// <summary>Documented power scheme APIs (powrprof.dll).</summary>
internal static class PowerNative
{
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
    private static extern uint PowerDeleteScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

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

    /// <summary>The plans Windows lists on this PC, each with the name Windows shows for it.</summary>
    internal static IReadOnlyList<PowerScheme> ListSchemes()
        => EnumerateSchemes().Select(id => new PowerScheme(id, GetFriendlyName(id))).ToList();

    /// <summary>
    /// Makes Optima's copy of Ultimate Performance where Windows hides the built-in plan, and says
    /// whether Windows then lists it. A PC with Modern Standby accepts the copy and hides it like
    /// the original; a hidden copy can never be made active, so it is removed again instead of
    /// being left behind in the power settings.
    /// </summary>
    internal static bool TryCreateUltimatePerformance()
    {
        var source = PowerPlanPolicy.UltimatePerformance;
        var copy = PowerPlanPolicy.OptimaUltimatePerformance;

        // Duplicated under a GUID of Optima's own: left to pick one, Windows makes a new plan with a
        // random GUID on every call, and nothing here could find it again the next time.
        var destPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(copy, destPtr, fDeleteOld: false);
            _ = PowerDuplicateScheme(IntPtr.Zero, ref source, ref destPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(destPtr);
        }

        // The return code is not the answer: the duplicate can succeed and still not be listed.
        if (EnumerateSchemes().Contains(copy))
        {
            return true;
        }
        _ = PowerDeleteScheme(IntPtr.Zero, ref copy);
        return false;
    }
}
