namespace Optima.Core.Detection;

/// <summary>
/// Decides what an install does with the virtual display's existing root device nodes. Kept apart
/// from the SetupAPI code in the elevated helper so the decision can be tested without a device.
/// </summary>
public static class VddRootNodes
{
    /// <summary>Device Manager's "disabled" code: a choice, not a fault, and the state a fresh install leaves.</summary>
    public const int ProblemDisabled = 22;

    /// <summary>
    /// CM_PROB_NEED_RESTART: the driver is in place and Windows only waits for a restart to start it.
    /// Not a fault, so a node in this state is neither rebuilt nor reported as a failed install.
    /// </summary>
    public const int ProblemNeedRestart = 14;

    /// <summary>One root node carrying the driver's hardware id. <paramref name="ProblemCode"/> is 0 when it works.</summary>
    public sealed record Node(string InstanceId, int ProblemCode, bool HasDriver);

    /// <summary>
    /// True only for exactly one node that has a driver and is working, merely disabled, or waiting
    /// for a restart, and only when no rebuild was asked for. Anything else is rebuilt: a node without
    /// a driver or with a fault never recovers by updating the package (the old "already present"
    /// answer kept such a node forever), and a second node would put a second virtual display on the
    /// desktop. <paramref name="rebuild"/> is set when the user pressed Install driver: Optima's own
    /// restore disables a faulty node again, and code 22 then hides the fault (43, 52, ...) that the
    /// reinstall was meant to clear, so keeping a "disabled" node would repeat the failure forever.
    /// </summary>
    public static bool CanKeep(IReadOnlyList<Node> nodes, bool rebuild = false)
        => !rebuild && nodes.Count == 1 && (IsUsable(nodes[0]) || IsWaitingForRestart(nodes[0]));

    /// <summary>
    /// Null when a freshly created node is usable, else a sentence naming what is wrong. A node waiting
    /// for a restart (code 14) is usable, and so is a node with no driver bound yet when the install
    /// itself asked for a restart: Windows finishes the binding at the restart, so the install is
    /// correct and the answer the user needs is "restart", not "install failed".
    /// </summary>
    public static string? CheckFresh(IReadOnlyList<Node> nodes, bool restartPending = false)
    {
        if (nodes.Count == 0)
        {
            return "Windows did not keep the virtual display device after creating it.";
        }
        if (nodes.Count > 1)
        {
            return $"Windows now lists {nodes.Count} virtual display devices instead of one.";
        }
        var node = nodes[0];
        if (IsWaitingForRestart(node) || (restartPending && !node.HasDriver))
        {
            return null;
        }
        if (!node.HasDriver)
        {
            return $"Windows created the virtual display device but did not bind the driver to it (problem code {node.ProblemCode}).";
        }
        return IsUsable(node)
            ? null
            : $"The virtual display device was created but Windows reports problem code {node.ProblemCode}.";
    }

    /// <summary>True when any node is waiting for a restart, so the install reports one as owed.</summary>
    public static bool NeedsRestart(IReadOnlyList<Node> nodes)
        => nodes.Any(n => n.ProblemCode == ProblemNeedRestart);

    private static bool IsUsable(Node node)
        => node.HasDriver && node.ProblemCode is 0 or ProblemDisabled;

    private static bool IsWaitingForRestart(Node node)
        => node.ProblemCode == ProblemNeedRestart;
}
