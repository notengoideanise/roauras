using System.Runtime.InteropServices;

namespace RefugeCooldownOverlay.Core;

/// <summary>
/// Bounded, read-only timer-list sampler. Walks the client's linked timer list
/// (sentinel at manager-relative preferred VA 0x1107A34; nodes: +0 next, +4 prev,
/// +8 skill key, +0xC expiry tick) with structural validation modeled on the
/// host-validated Gravekeeper reader. Never scans memory and never writes.
/// </summary>
public sealed class TimerReader
{
    private readonly ITimerMemory _memory;
    private readonly ProcessProfile _profile;
    private readonly nuint _moduleBase;
    private readonly nuint _listRoot;
    private readonly Func<uint> _clock;

    public TimerReader(ProcessProfile profile, ITimerMemory memory, nuint moduleBase)
        : this(profile, memory, moduleBase, clock: null)
    {
    }

    public TimerReader(ProcessProfile profile, ITimerMemory memory, nuint moduleBase, Func<uint>? clock)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _moduleBase = moduleBase;
        _listRoot = profile.Relocate(profile.TimerListRootPreferredVa, moduleBase);
        _clock = clock ?? DefaultClock;
    }

    /// <summary>Client-aligned monotonic tick source. Injected in offline tests.</summary>
    public uint ClockTick => _clock();

    private static uint DefaultClock()
    {
        // winmm!timeGetTime is the client's own clock source (import 0xD0754C).
        return NativeMethods.TimeGetTime();
    }

    /// <summary>
    /// Take one validated snapshot. The manager/game-mode pointer chain is checked
    /// first (same chain as the host-validated Gravekeeper reader: manager+0x58 == 1,
    /// mode = [manager+4], [mode] == relocated CGameMode vtable); any structural
    /// anomaly returns an invalid snapshot — never a partial list, and never an
    /// implicit "all ready".
    /// </summary>
    public TimerSnapshot ReadSnapshot()
    {
        if (!ValidateGameChain(out var chainError))
        {
            return TimerSnapshot.Invalid(chainError, ClockTick);
        }

        if (!_memory.TryReadUInt32(_listRoot, out var head) ||
            !_memory.TryReadUInt32(_listRoot + 4, out var count))
        {
            return TimerSnapshot.Invalid("timer list header unreadable", ClockTick);
        }

        if (count > (uint)_profile.MaxTimerNodes)
        {
            return TimerSnapshot.Invalid($"timer list exceeds safe bound ({count} nodes)", ClockTick);
        }

        if (count == 0)
        {
            // Empty list: sentinel must self-link.
            if (!_memory.TryReadUInt32(head, out var firstEmpty) ||
                !_memory.TryReadUInt32(head + 4, out var lastEmpty) ||
                firstEmpty != head || lastEmpty != head)
            {
                return TimerSnapshot.Invalid("empty timer list has inconsistent sentinel", ClockTick);
            }

            return new TimerSnapshot(new Dictionary<uint, uint>(), ClockTick, IsValid: true);
        }

        if (!_memory.TryReadUInt32(head, out var node))
        {
            return TimerSnapshot.Invalid("timer list first node unreadable", ClockTick);
        }

        var timers = new Dictionary<uint, uint>();
        var visited = new HashSet<nuint>();
        nuint previous = head;
        var firstNode = node;
        uint walked = 0;

        while (node != head)
        {
            if (walked >= (uint)_profile.MaxTimerNodes ||
                !visited.Add(node) ||
                !_memory.TryReadUInt32(node + 4, out var backlink) ||
                backlink != previous ||
                !_memory.TryReadUInt32(node + 8, out var key) ||
                !_memory.TryReadUInt32(node + 12, out var expiry))
            {
                return TimerSnapshot.Invalid("timer list traversal invalid or unreadable", ClockTick);
            }

            if (timers.ContainsKey(key))
            {
                return TimerSnapshot.Invalid($"duplicate timer key {key}", ClockTick);
            }

            timers[key] = expiry;

            // Stability re-reads: a concurrent mutation invalidates the sample.
            if (!_memory.TryReadUInt32(node, out var next) ||
                !_memory.TryReadUInt32(node + 8, out var key2) || key2 != key ||
                !_memory.TryReadUInt32(node + 12, out var expiry2) || expiry2 != expiry)
            {
                return TimerSnapshot.Invalid("timer list changed during sample", ClockTick);
            }

            previous = node;
            node = next;
            walked++;
        }

        // Post-walk header/sentinel consistency.
        if (!_memory.TryReadUInt32(_listRoot, out var head2) || head2 != head ||
            !_memory.TryReadUInt32(_listRoot + 4, out var count2) || count2 != count ||
            !_memory.TryReadUInt32(head, out var firstAgain) || firstAgain != firstNode ||
            !_memory.TryReadUInt32(head + 4, out var last) || last != previous ||
            walked != count)
        {
            return TimerSnapshot.Invalid("timer list header changed during sample", ClockTick);
        }

        return new TimerSnapshot(timers, ClockTick, IsValid: true);
    }

    /// <summary>
    /// Bounded read-only validation of the manager → CGameMode chain, mirroring the
    /// host-validated reader's Validate() before any timer sample is accepted.
    /// </summary>
    private bool ValidateGameChain(out string error)
    {
        var manager = _profile.Relocate(_profile.ManagerPreferredVa, _moduleBase);
        if (!_memory.TryReadUInt32(manager + 0x58, out var managerState) || managerState != 1)
        {
            error = "manager state invalid; game object changed";
            return false;
        }

        if (!_memory.TryReadUInt32(manager + 4, out var gameMode))
        {
            error = "manager game-mode pointer unreadable";
            return false;
        }

        var expectedVtable = _profile.Relocate(_profile.GameModeVtablePreferredVa, _moduleBase);
        if (!_memory.TryReadUInt32(gameMode, out var vtable) || vtable != expectedVtable)
        {
            error = "game-mode vtable mismatch; re-arm in game";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static class NativeMethods
    {
        [DllImport("winmm.dll", EntryPoint = "timeGetTime")]
        public static extern uint TimeGetTime();
    }
}
