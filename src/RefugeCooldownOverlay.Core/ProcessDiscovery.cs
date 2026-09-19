namespace RefugeCooldownOverlay.Core;

/// <summary>
/// Pure process-name discovery policy. The default selection ("PRM", or empty)
/// accepts the plain client and any patch-named executable (PRM-patch*), because
/// the current client ships as PRM-patch16.exe and future patches keep that
/// pattern. Discovery is name-based convenience only: the exact SHA-256 profile
/// gate in ProcessTimerMemory.TryAttach remains the real trust boundary and runs
/// BEFORE the process is opened. An explicit non-default ProcessName is matched
/// exactly, so users can pin a specific executable or a non-PRM test process.
/// </summary>
public static class ProcessDiscovery
{
    public const string DefaultProcessName = "PRM";
    private const string PatchPrefix = "PRM-patch";

    /// <summary>True when the configured name is empty or the documented default.</summary>
    public static bool IsDefaultSelection(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ||
        configured.Trim().Equals(DefaultProcessName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pure name matcher. Default: "PRM" or "PRM-patch&lt;suffix&gt;". Explicit: exact
    /// (case-insensitive) configured name only.
    /// </summary>
    public static bool Matches(string? processName, string? configured)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var name = processName.Trim();
        if (IsDefaultSelection(configured))
        {
            return name.Equals(DefaultProcessName, StringComparison.OrdinalIgnoreCase) ||
                (name.StartsWith(PatchPrefix, StringComparison.OrdinalIgnoreCase) &&
                 name.Length > PatchPrefix.Length);
        }

        return name.Equals(configured!.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
