namespace RefugeCooldownOverlay.Core;

/// <summary>
/// Projects timer snapshots into display states. The live expiry tick from the
/// client's clock is the sole countdown authority; database/base cooldown durations
/// are never consulted.
/// </summary>
public static class CooldownProjector
{
    /// <summary>
    /// Unsigned modular remaining time, matching the validated reader semantics:
    /// an expiry more than half a tick-wrap in the past means "not on cooldown".
    /// </summary>
    public static uint Remaining(uint expiry, uint tick)
    {
        var delta = (expiry - tick) & 0xFFFFFFFF;
        return delta >= 0x80000000 ? 0u : delta;
    }

    public static IReadOnlyList<SkillCooldownState> Project(
        IReadOnlyList<SkillDefinition> selected, TimerSnapshot snapshot)
    {
        var states = new List<SkillCooldownState>(selected.Count);
        foreach (var skill in selected)
        {
            states.Add(Project(skill, snapshot));
        }

        return states;
    }

    public static SkillCooldownState Project(SkillDefinition skill, TimerSnapshot snapshot)
    {
        switch (skill.MappingStatus)
        {
            case SkillMappingStatus.Custom:
            case SkillMappingStatus.Unknown:
                return new SkillCooldownState(
                    skill, CooldownDisplayState.MappingUnavailable, null, null,
                    Detail: "No reliable runtime key mapping.");
        }

        if (!snapshot.IsValid)
        {
            return new SkillCooldownState(
                skill, CooldownDisplayState.SamplingUnavailable, null, null,
                Detail: snapshot.Error ?? "Timer sample unavailable.");
        }

        if (skill.RuntimeKey is not int key)
        {
            return new SkillCooldownState(
                skill, CooldownDisplayState.MappingUnavailable, null, null,
                Detail: "Mapped skill missing runtime key (catalog error).");
        }

        if (snapshot.TryGetExpiry((uint)key, out var expiry))
        {
            return new SkillCooldownState(
                skill, CooldownDisplayState.Cooldown, expiry,
                Remaining(expiry, snapshot.Tick));
        }

        return new SkillCooldownState(
            skill,
            skill.MappingStatus == SkillMappingStatus.StaticInferred
                ? CooldownDisplayState.NeedsValidation
                : CooldownDisplayState.Ready,
            null,
            null,
            Detail: skill.MappingStatus == SkillMappingStatus.StaticInferred
                ? "No live timer observed for inferred key."
                : "No live timer for validated key in current sample.");
    }
}
