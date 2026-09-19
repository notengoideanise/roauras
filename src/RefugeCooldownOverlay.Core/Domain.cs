namespace RefugeCooldownOverlay.Core;

/// <summary>Evidence tier for mapping a catalog skill to a client runtime timer key.</summary>
public enum SkillMappingStatus
{
    /// <summary>Key observed live on a validated host recording for a supported build.</summary>
    Validated,

    /// <summary>Key inferred from static client/binary evidence, not individually live-correlated.</summary>
    StaticInferred,

    /// <summary>Client-specific PRM_* handle or nonstandard skill with no safe numeric resolution.</summary>
    Custom,

    /// <summary>No reliable key resolution exists.</summary>
    Unknown,
}

/// <summary>Display state for one selected skill.</summary>
public enum CooldownDisplayState
{
    /// <summary>Live client timer node present; RemainingMilliseconds is authoritative.</summary>
    Cooldown,

    /// <summary>Validated mapping, valid sample, no live timer node.</summary>
    Ready,

    /// <summary>Sample invalid/unavailable; never presented as ready.</summary>
    SamplingUnavailable,

    /// <summary>Custom/unknown mapping; runtime key unresolved.</summary>
    MappingUnavailable,

    /// <summary>Static-inferred key; usable for observation but not authoritative until validated.</summary>
    NeedsValidation,
}

/// <summary>One catalog skill. Catalog availability is independent of runtime-key proof.</summary>
/// <param name="IconHandle">Client SKID name (e.g. PR_LEXAETERNA); icon lookup key.</param>
/// <param name="IconFile">Packaged icon file name under ./icons, or null when the client ships no art.</param>
public sealed record SkillDefinition(
    string Slug,
    string Name,
    string ClassName,
    string IconHandle,
    int? RuntimeKey,
    SkillMappingStatus MappingStatus,
    string? Evidence = null,
    string? IconFile = null);

/// <summary>One timer-list node observed in a validated sample.</summary>
public readonly record struct TimerEntry(uint Key, uint ExpiryTick);

/// <summary>
/// Bounded snapshot of the client timer list. An invalid sample carries an error and
/// must never be interpreted as "all skills ready".
/// </summary>
public sealed record TimerSnapshot(
    IReadOnlyDictionary<uint, uint> Timers,
    uint Tick,
    bool IsValid,
    string? Error = null)
{
    public static TimerSnapshot Invalid(string error, uint tick = 0) =>
        new(new Dictionary<uint, uint>(), tick, IsValid: false, error);

    public bool TryGetExpiry(uint key, out uint expiry)
    {
        if (IsValid && Timers.TryGetValue(key, out expiry))
        {
            return true;
        }

        expiry = 0;
        return false;
    }
}

/// <summary>Projected display state for one selected skill.</summary>
public sealed record SkillCooldownState(
    SkillDefinition Skill,
    CooldownDisplayState State,
    uint? ExpiryTick,
    uint? RemainingMilliseconds,
    string? Detail = null);
