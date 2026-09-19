using System.Diagnostics;
using RefugeCooldownOverlay.Core;

namespace RefugeCooldownOverlay.App;

/// <summary>
/// Polling lifecycle: attach to the allowlisted PRM build, sample the timer list,
/// project display states, and drive the overlay. Read-only; no input, no writes,
/// no injection, no network.
///
/// Visibility is PRM-scoped: the overlay is shown only while the attached PRM main
/// window is the foreground window and not minimized, and hidden on Alt-Tab, other
/// foreground windows, missing/minimized/closed PRM. No global always-on-top.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private readonly OverlaySettings _settings;
    private readonly OverlayForm _overlay;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Func<bool> _isConfiguring;
    private IReadOnlyList<SkillDefinition> _selected = Array.Empty<SkillDefinition>();
    private ProcessTimerMemory? _memory;
    private TimerReader? _reader;
    private int? _attachedPid;
    private string? _attachedPath;
    private (long Length, DateTime ModifiedUtc)? _attachedStamp;
    private string? _lastError;

    // Rate-limited visibility logging: report transitions, not every 50 ms poll.
    private bool? _lastVisibleState;
    private int _sampleLogCounter;

    public OverlayController(
        OverlaySettings settings,
        OverlayForm overlay,
        IReadOnlyList<SkillDefinition> selectedSkills,
        Action<string>? reportStatus,
        Func<bool>? isConfiguring = null)
    {
        _settings = settings;
        _overlay = overlay;
        _selected = selectedSkills;
        _isConfiguring = isConfiguring ?? (() => false);
        _timer = new System.Windows.Forms.Timer { Interval = settings.PollMilliseconds };
        _timer.Tick += (_, _) => PollOnce();
        _ = reportStatus; // status text UI removed; diagnostics go to OverlayLog
    }

    public void Start() => _timer.Start();

    public void SetSelectedSkills(IReadOnlyList<SkillDefinition> selected) => _selected = selected;

    /// <summary>Reapply attachment, visibility, and current layout immediately.</summary>
    public void RefreshNow() => PollOnce();

    /// <summary>Apply an updated poll interval live; clamped to the same 25–1000 ms bounds.</summary>
    public void ApplyPollInterval(int pollMilliseconds)
    {
        _timer.Interval = Math.Clamp(pollMilliseconds, 25, 1000);
    }

    public string LastError => _lastError ?? string.Empty;

    /// <summary>Attached PID, or null when detached. Used by anchor saving.</summary>
    public int? AttachedPid => _attachedPid;

    public void PollOnce()
    {
        try
        {
            if (!EnsureAttached())
            {
                // Nothing attachable: hide rather than imply any readiness state.
                SetOverlayVisible(false, "detached");
                return;
            }

            FollowTargetWindow();

            var snapshot = _reader!.ReadSnapshot();
            var states = CooldownProjector.Project(_selected, snapshot);
            _overlay.UpdateStates(states);

            // Rate-limited sample health logging: one line per ~20 s at 50 ms polls.
            if (++_sampleLogCounter % 400 == 0)
            {
                OverlayLog.Info(
                    $"sample pid={_attachedPid} valid={snapshot.IsValid} nodes={snapshot.Timers.Count}"
                    + (snapshot.IsValid
                        ? $" timerKeys={string.Join(',', snapshot.Timers.Keys.OrderBy(key => key))}"
                        : $" error={snapshot.Error}"));
                _sampleLogCounter = 0;
            }

            if (!snapshot.IsValid)
            {
                OverlayLog.Info($"invalid sample: {snapshot.Error}");
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            OverlayLog.Error($"poll failed: {ex}");
            ShowAll(CooldownDisplayState.SamplingUnavailable, ex.Message);
        }
    }

    /// <summary>
    /// Track the PRM window: the overlay is visible only when PRM is the foreground
    /// window, positioned at the saved anchor offset from PRM's top-left. Skipped in
    /// configuration mode so the user can drag the overlay freely.
    /// </summary>
    private void FollowTargetWindow()
    {
        if (_attachedPid is null)
        {
            return;
        }

        var hwnd = Win32Window.FindMainWindow(_attachedPid.Value);
        var targetUsable = hwnd != IntPtr.Zero && !Win32Window.IsIconic(hwnd);
        var targetForeground = targetUsable && Win32Window.GetForegroundWindow() == hwnd;

        if (!OverlayVisibilityPolicy.ShouldShow(_isConfiguring(), targetUsable, targetForeground))
        {
            SetOverlayVisible(false, hwnd == IntPtr.Zero ? "no-window" : "not-foreground");
            return;
        }

        if (Win32Window.GetWindowRect(hwnd, out var rect))
        {
            var (x, y) = AnchorMath.ScreenFromAnchor(
                _settings.AnchorX, _settings.AnchorY, rect.Left, rect.Top);
            if (!_isConfiguring())
            {
                _overlay.Location = new Point(x, y);
            }
            SetOverlayVisible(true, _isConfiguring() ? "configuration" : "foreground");
        }
    }

    private void SetOverlayVisible(bool visible, string reason)
    {
        if (_overlay.Visible != visible)
        {
            _overlay.Visible = visible;
            OverlayLog.Info($"overlay {(visible ? "shown" : "hidden")}: {reason}");
        }

        _lastVisibleState = visible;
    }

    /// <summary>Target window bounds, when attached and usable. Used by anchor saving.</summary>
    internal bool TryGetTargetWindowRect(out Win32Window.RECT rect)
    {
        rect = default;
        if (_attachedPid is null)
        {
            return false;
        }

        var hwnd = Win32Window.FindMainWindow(_attachedPid.Value);
        return hwnd != IntPtr.Zero && !Win32Window.IsIconic(hwnd) && Win32Window.GetWindowRect(hwnd, out rect);
    }

    private bool EnsureAttached()
    {
        if (_memory is not null && _attachedPid is not null)
        {
            if (AttachmentStillValid())
            {
                return true;
            }

            OverlayLog.Info($"detaching pid={_attachedPid} (identity changed or exited)");
            Detach();
        }

        // Default discovery accepts PRM.exe and PRM-patch*.exe (the current client
        // ships as PRM-patch16.exe); an explicit ProcessName is matched exactly.
        // The exact-hash gate inside TryAttach still runs BEFORE OpenProcess.
        var candidates = ProcessDiscovery.IsDefaultSelection(_settings.ProcessName)
            ? Process.GetProcesses()
                .Where(p => ProcessDiscovery.Matches(p.ProcessName, _settings.ProcessName))
                .ToList()
            : Process.GetProcessesByName(_settings.ProcessName).ToList();
        try
        {
            // Deterministic pick: lowest PID wins; multi-instance is logged.
            var process = candidates.OrderBy(c => c.Id).FirstOrDefault();
            if (process is null)
            {
                _lastError = $"No process matching '{_settings.ProcessName}' (default accepts PRM.exe and PRM-patch*.exe).";
                return false;
            }

            if (candidates.Count > 1)
            {
                OverlayLog.Info($"{_settings.ProcessName}: {candidates.Count} processes found; reading pid {process.Id}.");
            }

            var path = TryGetExecutablePath(process);
            if (path is null)
            {
                _lastError = "Cannot resolve PRM executable path for hashing.";
                return false;
            }

            // Exact-hash gate happens inside TryAttach BEFORE the process is opened.
            if (!ProcessTimerMemory.TryAttach(process.Id, path, out var memory, out var error)
                || memory is null)
            {
                _lastError = error;
                OverlayLog.Info($"attach refused pid={process.Id}: {error}");
                return false;
            }

            _memory = memory;
            _attachedPid = process.Id;
            _attachedPath = path;
            _attachedStamp = StampOf(path);
            _reader = new TimerReader(memory.Profile, memory, memory.ModuleBase);
            _lastError = string.Empty;
            OverlayLog.Info(
                $"attached pid={process.Id} profile={memory.Profile.Id} sha={memory.Sha256[..12]}…"
                + $" selected={_selected.Count} skills");
            return true;
        }
        finally
        {
            foreach (var candidate in candidates)
            {
                candidate.Dispose();
            }
        }
    }

    /// <summary>
    /// Poll-boundary revalidation. The file is re-hashed only when its identity
    /// (length + write time) changes since the previous hash; a 12 MB digest every
    /// 25 ms poll would burn a core for no safety gain. Any digest change must map
    /// to the same allowlisted profile or the attachment is dropped.
    /// </summary>
    private bool AttachmentStillValid()
    {
        var pid = _attachedPid!.Value;

        bool alive;
        string? path;
        try
        {
            using var process = Process.GetProcessById(pid);
            alive = !process.HasExited;
            path = alive ? TryGetExecutablePath(process) : null;
        }
        catch (ArgumentException)
        {
            // GetProcessById throws once the PID no longer exists; treat as exited.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (!alive)
        {
            return false;
        }

        if (path is null || !string.Equals(path, _attachedPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stamp = StampOf(path);
        if (_attachedStamp.HasValue && stamp == _attachedStamp.Value)
        {
            // File identity unchanged since the digest was confirmed.
            return true;
        }

        string currentSha;
        try
        {
            currentSha = ProcessTimerMemory.HashFile(path);
        }
        catch (Exception)
        {
            return false;
        }

        var detach = AttachPolicy.ShouldDetach(
            processAlive: true,
            currentExecutablePath: path,
            currentSha256: currentSha,
            attachedSha256: _memory!.Sha256);

        if (!detach)
        {
            _attachedStamp = stamp;
        }

        return !detach;
    }

    private static (long Length, DateTime ModifiedUtc)? StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return (info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private void ShowAll(CooldownDisplayState state, string detail)
    {
        var states = _selected.Select(skill => new SkillCooldownState(
            skill, state, null, null, detail)).ToList();
        _overlay.UpdateStates(states);
    }

    private void Detach()
    {
        _memory?.Dispose();
        _memory = null;
        _reader = null;
        _attachedPid = null;
        _attachedPath = null;
        _attachedStamp = null;
        _lastVisibleState = null;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        Detach();
    }
}
