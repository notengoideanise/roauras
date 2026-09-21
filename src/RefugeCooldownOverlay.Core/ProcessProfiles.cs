namespace RefugeCooldownOverlay.Core;

/// <summary>
/// Exact-build memory-layout profile. Addresses are preferred VAs from the static
/// evidence in Gravekeeper-StateProbe-README.md / compatibility-regions.json; the
/// reader relocates them by module base. An unknown binary must never reach a read.
/// </summary>
public sealed record ProcessProfile(
    string Id,
    string DisplayName,
    uint TimerListRootPreferredVa,
    uint ManagerPreferredVa,
    uint GameModeVtablePreferredVa,
    uint PreferredImageBase,
    int MaxTimerNodes)
{
    public nuint Relocate(uint preferredVa, nuint moduleBase) =>
        moduleBase + (nuint)(preferredVa - PreferredImageBase);
}

/// <summary>
/// Registry of exact SHA-256 allowlisted client builds. Patch 15 and Patch 16 are
/// byte-identical to Patch 14 (same digest); Patch 18 and Patch 19 have matching
/// static timer regions and use the same offsets pending live Windows confirmation.
/// There is deliberately no version-string or file-size fallback.
/// </summary>
public static class ProcessProfiles
{
    public const string LegacySha256 =
        "5b3fbd6b63d0e409dd0dbea0bcb389bab61d8e37a36855fe925a0a2310ea4d9b";
    public const string Patch13Sha256 =
        "a0b100d4c8f7a947f9c2f8065529d432fd973e69809212d94991b465d10a47f9";
    public const string Patch14Sha256 =
        "205697800e6c02ebf17a7bea17a15c87008d67a0ce0b7c32a3666cdf70926755";
    public const string Patch18Sha256 =
        "7e96f64968558b88d6fe7d4bdc7a12a15231ee30f942159babe54a9c99b1cc90";
    public const string Patch19Sha256 =
        "5c7086ed403917c4df6ef84cb6b7d2dba3301140662c6334f8845ad588f2c373";

    private static readonly Dictionary<string, ProcessProfile> Registry = Build();

    private static Dictionary<string, ProcessProfile> Build()
    {
        var legacy = new ProcessProfile(
            "legacy", "Legacy (pre-patch 13)",
            TimerListRootPreferredVa: 0x1107A34,
            ManagerPreferredVa: 0x0E78D88,
            GameModeVtablePreferredVa: 0x00D4D378,
            PreferredImageBase: 0x00400000,
            MaxTimerNodes: 128);
        var patch13 = legacy with { Id = "patch13", DisplayName = "Patch 13" };
        var patch14 = legacy with
        {
            Id = "patch14",
            DisplayName = "Patch 14 / Patch 15 / Patch 16",
        };
        var patch18 = patch14 with
        {
            Id = "patch18",
            DisplayName = "Patch 18 (static match; Windows validation pending)",
        };
        var patch19 = patch18 with
        {
            Id = "patch19",
            DisplayName = "Patch 19 (static match; Windows validation pending)",
        };

        var entries = new Dictionary<string, ProcessProfile>(StringComparer.OrdinalIgnoreCase)
        {
            [LegacySha256] = legacy,
            [Patch13Sha256] = patch13,
            [Patch14Sha256] = patch14,
            [Patch18Sha256] = patch18,
            [Patch19Sha256] = patch19,
        };
        return entries;
    }

    /// <summary>Exact, case-insensitive digest lookup. Near matches return null.</summary>
    public static ProcessProfile? TryGetByHash(string sha256)
    {
        var normalized = sha256.Trim().ToLowerInvariant();
        return Registry.TryGetValue(normalized, out var profile) ? profile : null;
    }

    /// <summary>True only when the digest is an exact allowlisted build.</summary>
    public static bool IsSupported(string sha256) => TryGetByHash(sha256) is not null;
}
