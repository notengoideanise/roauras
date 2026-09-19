namespace RefugeCooldownOverlay.Core;

/// <summary>
/// Pure attach/detach decisions for the overlay controller.
///
/// The attached executable's exact digest must be confirmable at every poll
/// boundary: the profile gate must hold before any further process memory read.
/// A missing process, an unresolvable executable path, or any digest change
/// forces detach and a fresh exact-hash attach.
/// </summary>
public static class AttachPolicy
{
    public static bool ShouldDetach(
        bool processAlive,
        string? currentExecutablePath,
        string? currentSha256,
        string attachedSha256)
    {
        if (!processAlive)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(currentExecutablePath))
        {
            return true;
        }

        if (string.IsNullOrEmpty(currentSha256))
        {
            return true;
        }

        return !string.Equals(currentSha256, attachedSha256, StringComparison.OrdinalIgnoreCase);
    }
}
