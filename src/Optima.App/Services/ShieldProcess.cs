using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Claims;
using System.Security.Principal;

namespace Optima.App.Services;

/// <summary>The two things the protected play loader needs from Windows: a way to run the module, and who is asking.</summary>
internal static class ShieldProcess
{
    private const string AdministratorsGroup = "S-1-5-32-544";

    /// <summary>Whether this build of Optima was made with the module beside it.</summary>
    internal static bool Bundled { get; } = typeof(ShieldProcess).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>()
        .Any(attribute => attribute is { Key: "ShieldBundled", Value: "true" });

    /// <summary>
    /// Runs Optima Shield with the given arguments, without a window. With <paramref name="wait"/>
    /// it returns what the module wrote once it has exited, giving it a few seconds; without, it
    /// returns at once. Null when the module could not be started.
    /// </summary>
    internal static string? Run(string exe, string arguments, bool wait)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = wait,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
            });
            if (process is null)
            {
                return null;
            }
            if (!wait)
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            return process.WaitForExit(6000) && output.Wait(1000) ? output.Result : string.Empty;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether this Windows account can approve an administrator prompt itself. Such an account's
    /// everyday token carries the Administrators group marked "deny only"; an account that is not an
    /// administrator would be asked for someone else's password instead, and is never asked.
    /// </summary>
    internal static bool AccountIsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
            || identity.Claims.Any(claim => claim.Type == ClaimTypes.DenyOnlySid && claim.Value == AdministratorsGroup);
    }
}
