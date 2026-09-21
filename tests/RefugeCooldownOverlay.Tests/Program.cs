// Offline test runner for the read-only cooldown overlay core.
// No external test package, no PRM process, no Windows-specific calls.
using System.Globalization;
using System.Text.Json;
using RefugeCooldownOverlay.Core;

var failures = new List<string>();
int passed = 0;

void Run(string name, Action test)
{
    try
    {
        test();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

uint TestClock = 5_000;

void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

TimerSnapshot SnapshotWith(Dictionary<uint, uint> timers, uint tick) =>
    new(timers, tick, IsValid: true);

// ---------- Domain ----------

Run("StatusNamesAndImmutableModels", () =>
{
    var skill = new SkillDefinition(
        "gravekeeper/loki-s-mark", "Loki's Mark", "Gravekeeper", "PR_LEXAETERNA",
        78, SkillMappingStatus.Validated);
    Assert(skill.RuntimeKey == 78, "runtime key retained");
    Assert(skill.MappingStatus == SkillMappingStatus.Validated, "mapping status retained");
    var invalid = TimerSnapshot.Invalid("boom");
    Assert(!invalid.IsValid && invalid.Error == "boom", "invalid snapshot carries error");
});

// ---------- Remaining arithmetic ----------

Run("RemainingUsesExpiryNotDatabaseDuration", () =>
{
    // No static duration exists in this library; remaining time must follow expiry only.
    Assert(CooldownProjector.Remaining(10_000, 5_000) == 5_000, "expiry-tick remaining");
    Assert(CooldownProjector.Remaining(7_000, 5_000) == 2_000, "reduced cooldown follows expiry");
    Assert(CooldownProjector.Remaining(5_000, 5_000) == 0, "expired now");
    Assert(CooldownProjector.Remaining(4_999, 5_000) == 0, "just expired");
});

Run("WraparoundRemaining", () =>
{
    // Same signed modular semantics as the host-validated AHK reader:
    // a delta under half the wrap range counts forward (remaining),
    // half-or-over counts as elapsed (0).
    Assert(CooldownProjector.Remaining(100u, uint.MaxValue - 50u) == 151, "forward wrap 100-(2^32-51)=151");
    Assert(CooldownProjector.Remaining(300u, uint.MaxValue - 200u) == 501, "forward wrap 300-(2^32-201)=501");
    Assert(CooldownProjector.Remaining(4_999u, 5_000u) == 0, "one tick in the past -> 0");
    Assert(CooldownProjector.Remaining(0x80000000u, 0u) == 0, "half-range boundary counts elapsed");
    Assert(CooldownProjector.Remaining(0x7FFFFFFFu, 0u) == 0x7FFFFFFF, "under half-range forward");
    Assert(CooldownProjector.Remaining(0x80000001u, 0u) == 0, "over half-range treated as elapsed");
});

// ---------- TimerReader structural validation (synthetic memory) ----------

const uint ListRoot = 0x1107A34;

SyntheticMemory BuildList(params (uint key, uint expiry)[] entries)
{
    var mem = new SyntheticMemory();
    var sentinel = mem.Alloc();
    var nodes = new List<nuint>();
    for (int i = 0; i < entries.Length; i++)
    {
        nodes.Add(mem.Alloc());
    }

    mem.Write(ListRoot, (uint)sentinel);         // root -> sentinel
    mem.Write(ListRoot + 4, (uint)entries.Length); // root+4 -> count

    // Manager → CGameMode chain required by reader validation before sampling:
    // manager+0x58 == 1, mode = [manager+4], [mode] == relocated vtable 0xD4D378.
    mem.Write(0x0E78D88 + 0x58, 1u);
    var modeObject = mem.Alloc();
    mem.Write(0x0E78D88 + 4, (uint)modeObject);
    mem.Write(modeObject, 0xD4D378u);
    mem.GameModeObject = modeObject;

    // Sentinel: +0 first, +4 last.
    mem.Write(sentinel + 0, (uint)(nodes.Count > 0 ? nodes[0] : sentinel));
    mem.Write(sentinel + 4, (uint)(nodes.Count > 0 ? nodes[^1] : sentinel));

    for (int i = 0; i < nodes.Count; i++)
    {
        var node = nodes[i];
        mem.Write(node + 0, (uint)(i + 1 < nodes.Count ? nodes[i + 1] : sentinel));
        mem.Write(node + 4, (uint)(i == 0 ? sentinel : nodes[i - 1]));
        mem.Write(node + 8, entries[i].key);
        mem.Write(node + 12, entries[i].expiry);
        mem.RegisterNode(entries[i].key, node);
    }

    return mem;
}

Run("ValidLinkedListSnapshot", () =>
{
    var mem = BuildList((78u, 5_000u), (2326u, 9_000u));
    var reader = new TimerReader(ProcessProfiles.TryGetByHash(ProcessProfiles.Patch14Sha256)!, mem, 0x400000, () => TestClock);
    var snap = reader.ReadSnapshot();
    Assert(snap.IsValid, $"expected valid, got {snap.Error}");
    Assert(snap.TryGetExpiry(78, out var expiry) && expiry == 5_000, "key 78 expiry");
    Assert(snap.TryGetExpiry(2326, out var expiry2) && expiry2 == 9_000, "key 2326 expiry");
    Assert(snap.Timers.Count == 2, "node count");
});

Run("ValidEmptyListSnapshot", () =>
{
    var mem = BuildList();
    var reader = new TimerReader(ProcessProfiles.TryGetByHash(ProcessProfiles.Patch13Sha256)!, mem, 0x400000, () => TestClock);
    var snap = reader.ReadSnapshot();
    Assert(snap.IsValid, $"expected valid empty, got {snap.Error}");
    Assert(snap.Timers.Count == 0, "empty timers");
});

Run("RejectsCountMismatch", () =>
{
    var mem = BuildList((78u, 5_000u));
    mem.Write(ListRoot + 4, 3u); // header claims 3 nodes, list has 1
    var reader = new TimerReader(ProcessProfiles.TryGetByHash(ProcessProfiles.Patch14Sha256)!, mem, 0x400000, () => TestClock);
    var snap = reader.ReadSnapshot();
    Assert(!snap.IsValid, "count mismatch must be invalid");
});

Run("RejectsExcessiveNodeCount", () =>
{
    var mem = BuildList((78u, 5_000u));
    mem.Write(ListRoot + 4, 9999u);
    var reader = new TimerReader(ProcessProfiles.TryGetByHash(ProcessProfiles.Patch14Sha256)!, mem, 0x400000, () => TestClock);
    var snap = reader.ReadSnapshot();
    Assert(!snap.IsValid, "over-cap count must be invalid");
});

Run("RejectsDuplicateKey", () =>
{
    var mem = new SyntheticMemory();
    var sentinel = mem.Alloc();
    var a = mem.Alloc();
    var b = mem.Alloc();
    mem.Write(ListRoot, (uint)sentinel);
    mem.Write(ListRoot + 4, 2u);
    mem.Write(sentinel + 0, (uint)a);
    mem.Write(sentinel + 4, (uint)b);
    mem.Write(a + 0, (uint)b);
    mem.Write(a + 4, (uint)sentinel);
    mem.Write(a + 8, 78u);
    mem.Write(a + 12, 5_000u);
    mem.Write(b + 0, (uint)sentinel);
    mem.Write(b + 4, (uint)a);
    mem.Write(b + 8, 78u); // duplicate key
    mem.Write(b + 12, 7_000u);
    var reader = new TimerReader(ProcessProfiles.TryGetByHash(ProcessProfiles.Patch14Sha256)!, mem, 0x400000, () => TestClock);
    var snap = reader.ReadSnapshot();
    Assert(!snap.IsValid, "duplicate key must be invalid");
});

Run("RejectsConcurrentMutation", () =>
{
    var mem = BuildList((78u, 5_000u), (2326u, 9_000u));
    // Mutate a payload on first read-back but keep later reads stable:
    // simplest deterministic way is a flaky cell that returns different values
    // for consecutive reads, emulating a change during the sample.
    mem.MakeCellVolatile(78u, 5_000u, 5_500u);
    var reader = new TimerReader(ProcessProfiles.TryGetByHash(ProcessProfiles.Patch14Sha256)!, mem, 0x400000, () => TestClock);
    var snap = reader.ReadSnapshot();
    Assert(!snap.IsValid, "mutating list must be invalid");
});

Run("RejectsBacklinkMismatch", () =>
{
    var mem = BuildList((78u, 5_000u), (2326u, 9_000u));
    var nodeB = mem.FindNodeByKey(2326u)!.Value;
    mem.Write(nodeB + 4, (uint)nodeB); // wrong: should point at previous node
    var reader = new TimerReader(ProcessProfiles.TryGetByHash(ProcessProfiles.Patch14Sha256)!, mem, 0x400000, () => TestClock);
    var snap = reader.ReadSnapshot();
    Assert(!snap.IsValid, "backlink mismatch must be invalid");
});

Run("RejectsUnreadableHeader", () =>
{
    var mem = new SyntheticMemory(); // nothing mapped at ListRoot
    var reader = new TimerReader(ProcessProfiles.TryGetByHash(ProcessProfiles.Patch14Sha256)!, mem, 0x400000, () => TestClock);
    var snap = reader.ReadSnapshot();
    Assert(!snap.IsValid, "unreadable header must be invalid");
});

Run("RejectsManagerStateInvalid", () =>
{
    var mem = BuildList((78u, 5_000u));
    mem.Write(0x0E78D88 + 0x58, 0u); // manager state must be 1
    var reader = new TimerReader(ProcessProfiles.TryGetByHash(ProcessProfiles.Patch14Sha256)!, mem, 0x400000, () => TestClock);
    var snap = reader.ReadSnapshot();
    Assert(!snap.IsValid && snap.Error!.Contains("manager"), $"manager state must gate sampling, got: {snap.Error}");
});

Run("RejectsGameModeVtableMismatch", () =>
{
    var mem = BuildList((78u, 5_000u));
    mem.Write(mem.GameModeObject, 0xD4D379u); // one byte off the expected vtable
    var reader = new TimerReader(ProcessProfiles.TryGetByHash(ProcessProfiles.Patch14Sha256)!, mem, 0x400000, () => TestClock);
    var snap = reader.ReadSnapshot();
    Assert(!snap.IsValid && snap.Error!.Contains("vtable"), $"vtable mismatch must gate sampling, got: {snap.Error}");
});

// ---------- Attach policy (pure controller decisions) ----------

Run("AttachPolicyKeepsValidAttachment", () =>
{
    Assert(!AttachPolicy.ShouldDetach(processAlive: true, "C:\\PRM.exe", ProcessProfiles.Patch14Sha256, ProcessProfiles.Patch14Sha256),
        "alive + same digest -> stay attached");
});

Run("AttachPolicyDetachesOnProcessExit", () =>
{
    Assert(AttachPolicy.ShouldDetach(processAlive: false, "C:\\PRM.exe", ProcessProfiles.Patch14Sha256, ProcessProfiles.Patch14Sha256),
        "exited process -> detach");
});

Run("AttachPolicyDetachesOnHashChange", () =>
{
    Assert(AttachPolicy.ShouldDetach(processAlive: true, "C:\\PRM.exe", "205697800e6c02ebf17a7bea17a15c87008d67a0ce0b7c32a3666cdf70926754", ProcessProfiles.Patch14Sha256),
        "changed digest -> detach");
});

Run("AttachPolicyDetachesOnMissingPathOrHash", () =>
{
    Assert(AttachPolicy.ShouldDetach(processAlive: true, null, ProcessProfiles.Patch14Sha256, ProcessProfiles.Patch14Sha256), "missing path -> detach");
    Assert(AttachPolicy.ShouldDetach(processAlive: true, "C:\\PRM.exe", null, ProcessProfiles.Patch14Sha256), "missing digest -> detach");
});

Run("TryAttachRejectsUnknownHashBeforeOpen", () =>
{
    var temp = Path.Combine(Path.GetTempPath(), $"prm-unknown-{Guid.NewGuid():N}.exe");
    File.WriteAllBytes(temp, new byte[2048]);
    try
    {
        var ok = ProcessTimerMemory.TryAttach(1234, temp, out var reader, out var error);
        Assert(!ok && reader is null, "unknown digest must refuse attach");
        Assert(error.Contains("Unsupported", StringComparison.Ordinal), $"error must name unsupported build: {error}");
    }
    finally
    {
        File.Delete(temp);
    }
});

// ---------- Profile registry ----------

Run("ExactHashRegistryAcceptsKnownBuilds", () =>
{
    Assert(ProcessProfiles.TryGetByHash(ProcessProfiles.LegacySha256)!.Id == "legacy", "legacy");
    Assert(ProcessProfiles.TryGetByHash(ProcessProfiles.Patch13Sha256)!.Id == "patch13", "patch13");
    var p14 = ProcessProfiles.TryGetByHash(ProcessProfiles.Patch14Sha256)!;
    Assert(p14.Id == "patch14", "patch14 id");
    Assert(p14.DisplayName.Contains("Patch 16"), "patch16 alias carried by patch14 profile");
    Assert(ProcessProfiles.IsSupported(ProcessProfiles.Patch14Sha256), "patch16 hash == patch14 hash accepted");
    Assert(ProcessProfiles.TryGetByHash(ProcessProfiles.Patch18Sha256)!.Id == "patch18", "patch18 exact hash accepted");
    Assert(ProcessProfiles.TryGetByHash(ProcessProfiles.Patch19Sha256)!.Id == "patch19", "patch19 exact hash accepted");
});

Run("RejectsUnknownProfileBeforeOpen", () =>
{
    var nearMatch = "205697800e6c02ebf17a7bea17a15c87008d67a0ce0b7c32a3666cdf70926754"; // last byte differs
    Assert(ProcessProfiles.TryGetByHash(nearMatch) is null, "near match rejected");
    Assert(!ProcessProfiles.IsSupported(nearMatch), "near match unsupported");
    Assert(ProcessProfiles.TryGetByHash("not-a-hash") is null, "garbage rejected");
});

// ---------- CooldownProjector ----------

var validatedSkill = new SkillDefinition(
    "gravekeeper/loki-s-mark", "Loki's Mark", "Gravekeeper", "PR_LEXAETERNA",
    78, SkillMappingStatus.Validated);
var inferredSkill = new SkillDefinition(
    "executioner/omega", "Omega", "Executioner", "SR_FALLENEMPIRE",
    9999, SkillMappingStatus.StaticInferred);
var customSkill = new SkillDefinition(
    "shadow-set/touch-of-heinrich", "Touch of Heinrich", "Shadow set", "PA_LAND_PROTECTOR",
    null, SkillMappingStatus.Custom);

Run("CooldownFollowsLiveExpiry", () =>
{
    var snap = SnapshotWith(new Dictionary<uint, uint> { [78] = 7_000 }, 5_000);
    var state = CooldownProjector.Project(validatedSkill, snap);
    Assert(state.State == CooldownDisplayState.Cooldown, "on cooldown");
    Assert(state.RemainingMilliseconds == 2_000, "remaining from expiry");
    Assert(state.ExpiryTick == 7_000, "expiry retained");
});

Run("MissingValidatedKeyIsReady", () =>
{
    var snap = SnapshotWith(new Dictionary<uint, uint>(), 5_000);
    var state = CooldownProjector.Project(validatedSkill, snap);
    Assert(state.State == CooldownDisplayState.Ready, "ready when timer absent");
});

Run("InvalidSnapshotIsUnavailable", () =>
{
    var snap = TimerSnapshot.Invalid("changed during sample");
    var state = CooldownProjector.Project(validatedSkill, snap);
    Assert(state.State == CooldownDisplayState.SamplingUnavailable, "invalid sample not ready");
    Assert(state.RemainingMilliseconds is null, "no stale countdown");
});

Run("NoStaleReadyAfterInvalidSample", () =>
{
    var valid = SnapshotWith(new Dictionary<uint, uint> { [78] = 7_000 }, 5_000);
    var before = CooldownProjector.Project(validatedSkill, valid);
    Assert(before.State == CooldownDisplayState.Cooldown && before.RemainingMilliseconds == 2_000,
        "cooldown before invalidation");
    var after = CooldownProjector.Project(validatedSkill, TimerSnapshot.Invalid("changed during sample"));
    Assert(after.State == CooldownDisplayState.SamplingUnavailable,
        "a previously cooling skill must never show ready/stale after an invalid sample");
    Assert(after.RemainingMilliseconds is null && after.ExpiryTick is null, "no stale values carried");
});

Run("InferredKeyUsesObservedLiveExpiryOnly", () =>
{
    var snap = SnapshotWith(new Dictionary<uint, uint> { [9999] = 8_000 }, 5_000);
    var state = CooldownProjector.Project(inferredSkill, snap);
    Assert(state.State == CooldownDisplayState.Cooldown, "observed inferred key counts down");
    Assert(state.RemainingMilliseconds == 3_000, "remaining comes only from live expiry");

    var absent = CooldownProjector.Project(inferredSkill, SnapshotWith(new Dictionary<uint, uint>(), 5_000));
    Assert(absent.State == CooldownDisplayState.NeedsValidation, "absent inferred key is never claimed ready");
});

Run("UnknownKeyMappingUnavailable", () =>
{
    var unknown = customSkill with { MappingStatus = SkillMappingStatus.Unknown };
    var snap = SnapshotWith(new Dictionary<uint, uint> { [78] = 7_000 }, 5_000);
    Assert(CooldownProjector.Project(customSkill, snap).State == CooldownDisplayState.MappingUnavailable, "custom");
    Assert(CooldownProjector.Project(unknown, snap).State == CooldownDisplayState.MappingUnavailable, "unknown");
});

Run("ProjectAllSelectedSkills", () =>
{
    var snap = SnapshotWith(new Dictionary<uint, uint> { [78] = 6_000 }, 5_000);
    var states = CooldownProjector.Project(
        new[] { validatedSkill, inferredSkill, customSkill }, snap);
    Assert(states.Count == 3, "one state per selected skill");
    Assert(states[0].State == CooldownDisplayState.Cooldown, "validated on cooldown");
    Assert(states[1].State == CooldownDisplayState.NeedsValidation, "absent inferred key held");
    Assert(states[2].State == CooldownDisplayState.MappingUnavailable, "custom held");
});

// ---------- Countdown formatting ----------

Run("CooldownFormatKeeps100to119sNumeric", () =>
{
    Assert(CooldownFormat.Seconds(5_000) == "5.0", "sub-10s one decimal");
    Assert(CooldownFormat.Seconds(10_000) == "10", "10s numeric");
    Assert(CooldownFormat.Seconds(100_000) == "100", "100s must stay numeric, not 1m");
    Assert(CooldownFormat.Seconds(119_000) == "119", "119s must stay numeric");
    Assert(CooldownFormat.Seconds(120_000) == "2m00", "120s switches to minutes");
    Assert(CooldownFormat.Seconds(185_000) == "3m05", "3m05");
});

// ---------- Layout ----------

Run("LayoutOneColumn", () =>
{
    var placements = OverlayLayout.Calculate(3, 1, 48, 4);
    Assert(placements.Count == 3, "count");
    Assert(placements[0].X == 0 && placements[1].X == 0 && placements[2].X == 0, "single column x");
    Assert(placements[1].Y == 52 && placements[2].Y == 104, "single column y step");
});

Run("LayoutTwoColumnsPartialRow", () =>
{
    var placements = OverlayLayout.Calculate(5, 2, 32, 8);
    Assert(placements[0].X == 0 && placements[1].X == 40, "columns x");
    Assert(placements[2].Y == 40 && placements[4].X == 0, "row step and partial last row");
    // Stable ordering: same input, same order.
    var again = OverlayLayout.Calculate(5, 2, 32, 8);
    Assert(placements.Select(p => (p.X, p.Y)).SequenceEqual(again.Select(p => (p.X, p.Y))), "stable");
});

Run("LayoutClampsBadInput", () =>
{
    var placements = OverlayLayout.Calculate(2, 0, 48, 4);
    Assert(placements[1].Y == 52 && placements[1].X == 0, "columns 0 clamped to single column");
});

// ---------- Catalog artifact invariants ----------

 var CatalogPath = Path.Combine(AppContext.BaseDirectory, "../../../../../data/skill-catalog.json");
 var EvidencePath = Path.Combine(AppContext.BaseDirectory, "../../../../../data/skill-mapping-evidence.json");

Run("CatalogArtifactExistsAndValidJson", () =>
{
    var doc = JsonDocument.Parse(File.ReadAllText(CatalogPath));
    Assert(doc.RootElement.TryGetProperty("skills", out var skills), "skills array present");
    Assert(skills.ValueKind == JsonValueKind.Array && skills.GetArrayLength() >= 1400,
        $"expected >=1400 dump rows, got {skills.GetArrayLength()}");
});

Run("CatalogNeverUsesStaticCooldowns", () =>
{
    var text = File.ReadAllText(CatalogPath);
    Assert(!text.Contains("cooldownMilliseconds", StringComparison.OrdinalIgnoreCase),
        "catalog must not carry timer authority fields");
});

Run("KnownMappingsValidated", () =>
{
    using var doc = JsonDocument.Parse(File.ReadAllText(CatalogPath));
    var skills = doc.RootElement.GetProperty("skills");
    uint? mark = null, alpha = null, coffin = null;
    foreach (var s in skills.EnumerateArray())
    {
        var slug = s.GetProperty("slug").GetString();
        var status = s.GetProperty("mappingStatus").GetString();
        if (slug == "gravekeeper/loki-s-mark")
        {
            Assert(status == "Validated", "Mark validated");
            mark = s.GetProperty("runtimeKey").GetUInt32();
        }
        else if (slug == "executioner/alpha")
        {
            Assert(status == "Validated", "Alpha validated");
            alpha = s.GetProperty("runtimeKey").GetUInt32();
        }
        else if (slug == "gravekeeper/cursed-coffin")
        {
            Assert(status == "Validated", "Coffin validated");
            coffin = s.GetProperty("runtimeKey").GetUInt32();
        }
    }
    Assert(mark == 78 && alpha == 2326 && coffin == 2343,
        $"expected 78/2326/2343, got {mark}/{alpha}/{coffin}");
});

Run("EveryTimedSkillCarriesClientIdentity", () =>
{
    using var doc = JsonDocument.Parse(File.ReadAllText(CatalogPath));
    var mapped = 0;
    foreach (var s in doc.RootElement.GetProperty("skills").EnumerateArray())
    {
        var key = s.GetProperty("runtimeKey");
        if (key.ValueKind == JsonValueKind.Number)
        {
            mapped++;
            var status = s.GetProperty("mappingStatus").GetString();
            Assert(status is "Validated" or "StaticInferred", $"keyed row {key} must be Validated/StaticInferred, got {status}");
        }
    }

    Assert(mapped >= 1400, $"expected the dump's SKID space mapped, got {mapped}");
});

Run("EvidenceFileDocumentsMappingPolicy", () =>
{
    var text = File.ReadAllText(EvidencePath);
    Assert(text.Contains("78", StringComparison.Ordinal) && text.Contains("2326", StringComparison.Ordinal)
        && text.Contains("2343", StringComparison.Ordinal), "validated pairs documented");
    Assert(text.Contains("FOLLOWER_NPC_RESET", StringComparison.Ordinal) && text.Contains("GD_APPROVAL", StringComparison.Ordinal),
        "real SKIDs 9999/10000 documented as ordinary keys");
    Assert(text.Contains("iconCoverage", StringComparison.Ordinal), "icon coverage reported");
});

Run("IconArtifactsPackaged", () =>
{
    // Verified client art must be packaged beside the catalog, keyed by iconFile.
    var iconsDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(CatalogPath))!, "icons");
    Assert(Directory.Exists(iconsDir), "icons directory exists");
    var packaged = Directory.GetFiles(iconsDir, "*.bmp").Length;
    Assert(packaged >= 1000, $"expected >=1000 packaged icons, got {packaged}");

    using var doc = JsonDocument.Parse(File.ReadAllText(CatalogPath));
    var withArt = 0;
    var total = 0;
    foreach (var s in doc.RootElement.GetProperty("skills").EnumerateArray())
    {
        total++;
        var iconFile = s.GetProperty("iconFile");
        if (iconFile.ValueKind == JsonValueKind.String)
        {
            withArt++;
            Assert(File.Exists(Path.Combine(iconsDir, iconFile.GetString()!)),
                $"catalog references missing icon file {iconFile}");
        }
    }

    Assert(withArt * 4 >= total * 3, $"icon coverage too low: {withArt}/{total}");
});

// ---------- Process discovery policy (portable) ----------

Run("DiscoveryDefaultAcceptsPrmAndPatchNames", () =>
{
    Assert(ProcessDiscovery.Matches("PRM", null), "default accepts PRM.exe");
    Assert(ProcessDiscovery.Matches("prm", null), "default match is case-insensitive");
    Assert(ProcessDiscovery.Matches("PRM-patch16", null), "default accepts current PRM-patch16.exe");
    Assert(ProcessDiscovery.Matches("PRM-patch13", null), "default accepts other patch names");
    Assert(ProcessDiscovery.Matches("PRM-patch-whatever", null), "default accepts future patch names");
    Assert(ProcessDiscovery.IsDefaultSelection(null) && ProcessDiscovery.IsDefaultSelection("PRM")
        && ProcessDiscovery.IsDefaultSelection("prm") && ProcessDiscovery.IsDefaultSelection(""),
        "empty/default/unset all count as default selection");
});

Run("DiscoveryDefaultRejectsUnrelatedNames", () =>
{
    Assert(!ProcessDiscovery.Matches("PRM-spoof", null), "PRM-spoof is not a patch name pattern");
    Assert(!ProcessDiscovery.Matches("PRMPatch16", null), "missing dash rejected");
    Assert(!ProcessDiscovery.Matches("notepad", null), "unrelated process rejected");
    Assert(!ProcessDiscovery.Matches("PRM-patch", null), "bare prefix without suffix rejected");
    Assert(!ProcessDiscovery.Matches("", null) && !ProcessDiscovery.Matches(null, null),
        "empty/null process names rejected");
});

Run("DiscoveryExplicitOverrideIsExactOnly", () =>
{
    Assert(ProcessDiscovery.Matches("PRM-patch16", "PRM-patch16"), "explicit pinned name matches");
    Assert(ProcessDiscovery.Matches("prm-patch16", "PRM-PATCH16"), "explicit match is case-insensitive");
    Assert(!ProcessDiscovery.Matches("PRM", "PRM-patch16"), "plain PRM does not match pinned patch name");
    Assert(!ProcessDiscovery.Matches("PRM-patch13", "PRM-patch16"), "other patch names do not match pin");
    Assert(!ProcessDiscovery.Matches("notepad", "PRM-patch16"), "unrelated process rejected under pin");
    Assert(ProcessDiscovery.Matches("notepad", "notepad"), "explicit non-PRM name works for test targets");
    Assert(!ProcessDiscovery.IsDefaultSelection("PRM-patch16"), "pin is not the default selection");
});

// ---------- Module/executable path identity (portable) ----------

Run("ModuleIdentityAcceptsCurrentPatchExecutableNames", () =>
{
    // Main module may be PRM.exe or any patch-named executable; identity is by path.
    Assert(ModuleIdentity.MatchesExecutablePath(
        "C:\\Games\\Refuge\\PRM.exe", "C:\\Games\\Refuge\\PRM.exe"), "PRM.exe exact match");
    Assert(ModuleIdentity.MatchesExecutablePath(
        "C:\\Games\\Refuge\\PRM-patch16.exe", "C:\\Games\\Refuge\\PRM-patch16.exe"),
        "PRM-patch16.exe exact match");
    Assert(ModuleIdentity.MatchesExecutablePath(
        "c:\\games\\refuge\\prm-patch16.exe", "C:\\Games\\Refuge\\PRM-patch16.exe"),
        "case-insensitive match");
    Assert(ModuleIdentity.MatchesExecutablePath(
        "C:/Games/Refuge/PRM.exe", "C:\\Games\\Refuge\\PRM.exe"), "slash-normalized match");
});

Run("ModuleIdentityRejectsPathMismatch", () =>
{
    Assert(!ModuleIdentity.MatchesExecutablePath(
        "C:\\Games\\Refuge\\PRM-patch16.exe", "C:\\Games\\Refuge\\PRM.exe"),
        "different executable names must not match");
    Assert(!ModuleIdentity.MatchesExecutablePath(
        "C:\\Games\\Refuge\\PRM.exe", "C:\\Other\\PRM.exe"), "different directories must not match");
    Assert(!ModuleIdentity.MatchesExecutablePath(
        "C:\\Games\\Refuge\\PRM-spoof.exe", "C:\\Games\\Refuge\\PRM.exe"), "similar name rejected");
    Assert(!ModuleIdentity.MatchesExecutablePath(null, "C:\\PRM.exe"), "null module path rejected");
    Assert(!ModuleIdentity.MatchesExecutablePath("C:\\PRM.exe", ""), "empty executable path rejected");
});

// ---------- OverlaySettings JSON policy ----------

Run("OverlaySettingsLoadHonorsCamelCaseTemplate", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"overlay-settings-{Guid.NewGuid():N}.json");
    try
    {
        // Exact shape of the shipped data/overlay-settings.json template.
        File.WriteAllText(path,
            "{\n" +
            "  \"processName\": \"PRM\",\n" +
            "  \"selectedSkillSlugs\": [\"gravekeeper/loki-s-mark\", \"executioner/alpha\"],\n" +
            "  \"anchorX\": 120,\n" +
            "  \"anchorY\": 64,\n" +
            "  \"iconSize\": 48,\n" +
            "  \"columns\": 2,\n" +
            "  \"spacing\": 6,\n" +
            "  \"opacity\": 0.9,\n" +
            "  \"clickThrough\": true,\n" +
            "  \"pollMilliseconds\": 50\n" +
            "}");
        var settings = OverlaySettings.Load(path);
        Assert(settings.ProcessName == "PRM", "camelCase processName loaded");
        Assert(settings.SelectedSkillSlugs.Count == 2, "camelCase selectedSkillSlugs loaded");
        Assert(settings.SelectedSkillSlugs[0] == "gravekeeper/loki-s-mark", "slug preserved");
        Assert(settings.AnchorX == 120 && settings.AnchorY == 64, "camelCase anchors loaded");
        Assert(settings.PollMilliseconds == 50, "camelCase pollMilliseconds loaded");
    }
    finally
    {
        File.Delete(path);
    }
});

Run("OverlaySettingsClampsOutOfRangeValues", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"overlay-settings-{Guid.NewGuid():N}.json");
    try
    {
        File.WriteAllText(path,
            "{\n" +
            "  \"pollMilliseconds\": 5000,\n" +
            "  \"columns\": 99,\n" +
            "  \"iconSize\": 4,\n" +
            "  \"opacity\": 7.5\n" +
            "}");
        var settings = OverlaySettings.Load(path);
        Assert(settings.PollMilliseconds == 1000, "poll clamped to 1000");
        Assert(settings.Columns == 12, "columns clamped to 12");
        Assert(settings.IconSize == 16, "iconSize clamped to 16");
        Assert(settings.Opacity == 1.0, "opacity clamped to 1.0");
    }
    finally
    {
        File.Delete(path);
    }
});

Run("OverlaySettingsSaveWritesCamelCase", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"overlay-settings-{Guid.NewGuid():N}.json");
    try
    {
        new OverlaySettings { AnchorX = 77, AnchorY = 88, SelectedSkillSlugs = new List<string> { "a/b" } }.Save(path);
        var text = File.ReadAllText(path);
        Assert(text.Contains("\"anchorX\"", StringComparison.Ordinal), "save uses camelCase anchorX");
        Assert(text.Contains("\"selectedSkillSlugs\"", StringComparison.Ordinal), "save uses camelCase slugs");
        // Round-trip: saved file must load back with identical values.
        var loaded = OverlaySettings.Load(path);
        Assert(loaded.AnchorX == 77 && loaded.AnchorY == 88, "round-trip anchors");
        Assert(loaded.SelectedSkillSlugs.Count == 1 && loaded.SelectedSkillSlugs[0] == "a/b", "round-trip slugs");
    }
    finally
    {
        File.Delete(path);
    }
});

Run("ClickThroughPolicyPassesThroughOnlyInDisplayMode", () =>
{
    // Display mode must pass every hit-test through so clicks stay in the game;
    // configuration mode must keep the overlay interactive for dragging.
    Assert(ClickThroughPolicy.ShouldPassThrough(configurationActive: false), "display mode is click-through");
    Assert(!ClickThroughPolicy.ShouldPassThrough(configurationActive: true), "configuration mode is interactive");
});

Run("ConfigurationVisibilitySurvivesTrayFocus", () =>
{
    Assert(OverlayVisibilityPolicy.ShouldShow(configuring: true, targetUsable: true, targetForeground: false),
        "configuration stays visible while tray/configuration owns focus");
    Assert(!OverlayVisibilityPolicy.ShouldShow(configuring: false, targetUsable: true, targetForeground: false),
        "display mode hides when PRM is not foreground");
    Assert(!OverlayVisibilityPolicy.ShouldShow(configuring: true, targetUsable: false, targetForeground: false),
        "configuration hides when PRM is closed or minimized");
});

Run("TimerReaderUsesWindowsExportName", () =>
{
    var source = File.ReadAllText(Path.Combine("cooldown-overlay", "src", "RefugeCooldownOverlay.Core", "TimerReader.cs"));
    Assert(source.Contains("EntryPoint = \"timeGetTime\"", StringComparison.Ordinal),
        "winmm import must use exact case-sensitive export name");
});

Run("ThemePaletteBackgroundsAreDark", () =>
{
    // Regression guard for the dark theme: every background tone stays dark and
    // never equals pure black (the overlay transparency key) so cells remain visible.
    foreach (var background in OverlayTheme.Backgrounds)
    {
        Assert(OverlayTheme.IsDarkBackground(background), $"background {background} is dark");
        Assert(background is { R: > 0, G: > 0, B: > 0 }, $"background {background} is not the transparency key");
    }
    foreach (var border in OverlayTheme.Borders)
    {
        Assert(OverlayTheme.IsDarkBorder(border), $"border {border} is dark");
    }
});

Run("ThemePaletteTextIsMutedLightNotWhite", () =>
{
    // Text must be readable muted light — no channel at pure white, which the
    // old Brushes.White disposal bug also implicated.
    foreach (var text in OverlayTheme.TextColors)
    {
        Assert(OverlayTheme.IsMutedLightText(text), $"text {text} is muted light, not white");
    }
});

// ---------- PRM-relative anchor math ----------

Run("AnchorMathRoundTripsAgainstMovedPrmWindow", () =>
{
    // Overlay placement is always an offset from the PRM window's top-left,
    // never an absolute screen coordinate: moving PRM must move the overlay.
    var (ax, ay) = AnchorMath.AnchorFromScreen(520, 300, 500, 260);
    Assert(ax == 20 && ay == 40, "anchor offset from window origin");
    var (sx, sy) = AnchorMath.ScreenFromAnchor(ax, ay, 800, 600);
    Assert(sx == 820 && sy == 640, "same anchor keeps placement after PRM moves");
    // Negative offsets are legal (overlay dragged left/above the client edge).
    var (nx, ny) = AnchorMath.AnchorFromScreen(480, 240, 500, 260);
    Assert(nx == -20 && ny == -20, "negative anchor offsets preserved");
    var (rx, ry) = AnchorMath.ScreenFromAnchor(nx, ny, 500, 260);
    Assert(rx == 480 && ry == 240, "negative anchor round-trips");
});

// ---------- Fallback tile monogram ----------

Run("SkillMonogramIsDeterministicFallbackText", () =>
{
    // When no icon art is available the tile shows a short monogram, never
    // fabricated art and never the old name strip under the cell.
    Assert(SkillMonogram.For("Loki's Mark") == "LM", "two words -> initials");
    Assert(SkillMonogram.For("Heal") == "HE", "single word -> first two letters");
    Assert(SkillMonogram.For("  mass   grave  ") == "MG", "whitespace collapsed");
    Assert(SkillMonogram.For("") == "?", "empty -> question mark");
    Assert(SkillMonogram.For("A") == "A", "single letter unchanged");
    Assert(SkillMonogram.For(null) == "?", "null -> question mark");
});

// ---------- Row-limited layout ----------

Run("LayoutRowsZeroIsAutoAndDropsNothing", () =>
{
    var placements = OverlayLayout.Calculate(5, 2, 32, 8, 0, 0, rows: 0);
    Assert(placements.Count == 5, "auto rows keep every tile");
    Assert(placements[4].Y == 80, "third row used (index 4 of 2 columns)");
});

Run("LayoutRowsLimitedGridDropsOverflowTiles", () =>
{
    var placements = OverlayLayout.Calculate(5, 2, 32, 8, 0, 0, rows: 2);
    Assert(placements.Count == 4, "only columns*rows tiles placed");
    Assert(placements.All(p => p.Index < 4), "overflow indices dropped");
    Assert(placements[3].X == 40 && placements[3].Y == 40, "2x2 grid last cell");
    // Stable and index-ordered even when truncated.
    var again = OverlayLayout.Calculate(5, 2, 32, 8, 0, 0, rows: 2);
    Assert(placements.Select(p => p.Index).SequenceEqual(again.Select(p => p.Index)), "stable truncation");
});

// ---------- Bounded rolling log ----------

Run("OverlayLogUsesWritablePrimaryAndExposesDirectory", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), $"overlay-log-primary-{Guid.NewGuid():N}");
    try
    {
        OverlayLog.ResetForTests();
        OverlayLog.InitPreferred(dir, null);
        OverlayLog.Info("startup");
        Assert(OverlayLog.DirectoryPath == dir, "chosen log directory exposed");
        Assert(File.Exists(Path.Combine(dir, "overlay.log")), "first log creates file");
    }
    finally
    {
        OverlayLog.ResetForTests();
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
});

Run("OverlayLogRollsOverAtCapacityAndNeverThrows", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), $"overlay-log-{Guid.NewGuid():N}");
    try
    {
        OverlayLog.ResetForTests();
        OverlayLog.CapBytes = 300;
        OverlayLog.Init(dir);
        for (var i = 0; i < 40; i++)
        {
            OverlayLog.Info($"line-{i}-padding-padding-padding");
        }

        var main = new FileInfo(Path.Combine(dir, "overlay.log"));
        var backup = new FileInfo(Path.Combine(dir, "overlay.log.1"));
        Assert(backup.Exists, "backup created after rollover");
        Assert(main.Exists && main.Length <= 500, $"main stays bounded, was {main.Length}");

        // Uninitialized logger must swallow calls, never throw from UI paths.
        OverlayLog.ResetForTests();
        OverlayLog.Info("still alive");
        OverlayLog.Error("and errors");
    }
    finally
    {
        OverlayLog.ResetForTests();
        OverlayLog.CapBytes = 1024 * 1024;
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
});

// ---------- Settings: rows + ordered selection ----------

Run("OverlaySettingsBackdropRoundTrips", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"overlay-backdrop-{Guid.NewGuid():N}.json");
    try
    {
        new OverlaySettings { Backdrop = OverlayBackdropStyle.White, BackdropPadding = 9 }.Save(path);
        var loaded = OverlaySettings.Load(path);
        Assert(loaded.Backdrop == OverlayBackdropStyle.White, "white backdrop round-trips");
        Assert(loaded.BackdropPadding == 9, "backdrop padding round-trips");
    }
    finally
    {
        File.Delete(path);
    }
});

Run("CountdownTextIsLargerBrighterAndOutlined", () =>
{
    // Cooldown label must stay readable over dark icon art: bigger than the old
    // IconSize/4.5 scale, near-white fill, dark outline for contrast on any tile.
    Assert(CountdownStyle.FontScale >= 0.28f, $"font scale too small: {CountdownStyle.FontScale}");
    Assert(CountdownStyle.FontScale <= 0.45f, $"font scale unreasonable: {CountdownStyle.FontScale}");
    Assert(CountdownStyle.MinimumFontPoints >= 9f, "minimum font size keeps small icons readable");
    Assert(CountdownStyle.TextBrightness() >= 600, "countdown fill must be near-white");
    Assert(CountdownStyle.OutlineBrightness() <= 80, "outline must be near-black for contrast");
    Assert(CountdownStyle.OutlineOffset >= 1, "outline offset present");
    // 48px default icon must yield at least a 13pt countdown label.
    Assert(Math.Max(CountdownStyle.MinimumFontPoints, 48 * CountdownStyle.FontScale) >= 13f,
        "default icon size gives >=13pt countdown");
});

Run("BackdropPolicyHugsGridAndNeverUsesTransparencyKeyColor", () =>
{
    Assert(OverlayBackdropPolicy.Padding(OverlayBackdropStyle.None, 12) == 0, "none mode has zero padding");
    Assert(OverlayBackdropPolicy.Padding(OverlayBackdropStyle.Dark, 12) == 12, "dark keeps padding");
    Assert(OverlayBackdropPolicy.Padding(OverlayBackdropStyle.Dark, -4) == 0, "padding clamped >= 0");
    Assert(OverlayBackdropPolicy.PanelColor(OverlayBackdropStyle.None) is null, "none mode paints no panel");
    var dark = OverlayBackdropPolicy.PanelColor(OverlayBackdropStyle.Dark)!.Value;
    Assert(dark is not { R: 0, G: 0, B: 0 }, "dark panel never equals transparency key black");
    Assert(OverlayTheme.IsDarkBackground(dark), "dark panel stays dark");
    var white = OverlayBackdropPolicy.PanelColor(OverlayBackdropStyle.White)!.Value;
    Assert(white.R >= 200 && white.G >= 200 && white.B >= 200, "white panel is light");
    Assert(OverlayBackdropPolicy.CornerRadius(OverlayBackdropStyle.Dark) > 0, "dark panel rounds corners");
    Assert(OverlayBackdropPolicy.CornerRadius(OverlayBackdropStyle.None) == 0, "none mode no shape");
});

Run("ConfigurationHasSeparateApplyAndSaveButtons", () =>
{
    var source = File.ReadAllText(Path.Combine(
        "cooldown-overlay", "src", "RefugeCooldownOverlay.App", "ConfigurationForm.cs"));
    Assert(source.Contains("\"Apply\"", StringComparison.Ordinal), "dedicated Apply button exists");
    Assert(source.Contains("\"Save\"", StringComparison.Ordinal), "Save button exists");
    Assert(!source.Contains("\"Save & Close\"", StringComparison.Ordinal), "old Save & Close label removed");
    Assert(!source.Contains("\"Apply & Save\"", StringComparison.Ordinal), "old combined button removed");
    Assert(!source.Contains("MessageBox", StringComparison.Ordinal), "modal popups removed: Apply keeps dialog open for further tweaking");

    var program = File.ReadAllText(Path.Combine(
        "cooldown-overlay", "src", "RefugeCooldownOverlay.App", "Program.cs"));
    Assert(program.Contains("!configuring && updated.ClickThrough", StringComparison.Ordinal),
        "Apply keeps configuration drag mode active instead of restoring click-through");
});

Run("OverlayRemovesFixedStartupSize", () =>
{
    // The large fixed 320x120 startup size was the 'ugly black square' before
    // states arrived; the panel must size from content only.
    var source = File.ReadAllText(Path.Combine(
        "cooldown-overlay", "src", "RefugeCooldownOverlay.App", "OverlayForm.cs"));
    Assert(!source.Contains("new Size(320, 120)", StringComparison.Ordinal), "fixed startup size removed");
});

Run("PanelMeasureHugsOccupiedTilesNotCapacity", () =>
{
    // 48px tiles, 6px spacing, 6px padding, 3 columns.
    // 4 skills -> 3+1 rows: width spans 3 tiles, height spans 2 rows.
    var (w4, h4) = OverlayPanel.Measure(4, 3, 48, 6, 0, 6);
    Assert(w4 == 3 * 48 + 2 * 6 + 12, $"width hugs 3 occupied columns, got {w4}");
    Assert(h4 == 2 * 48 + 1 * 6 + 12, $"height hugs 2 occupied rows, got {h4}");

    // 2 skills at 5 columns -> only 2 columns wide; no empty dark cells reserved.
    var (w2, _2) = OverlayPanel.Measure(2, 5, 48, 6, 0, 6);
    Assert(w2 == 2 * 48 + 1 * 6 + 12, $"no reserved empty cells, got {w2}");

    // Rows cap drops overflow: 5 skills, 2 cols, 2 rows -> only 4 placed.
    var (wc, hc) = OverlayPanel.Measure(5, 2, 32, 8, 2, 6);
    Assert(wc == 2 * 32 + 8 + 12 && hc == 2 * 32 + 8 + 12, "panel hugs only placed tiles");

    // No skills -> minimal footprint, never a large fixed rectangle.
    var (w0, h0) = OverlayPanel.Measure(0, 3, 48, 6, 0, 6);
    Assert(w0 <= 16 && h0 <= 16, "empty state shrinks to minimal size");

    // Transparent mode: zero padding, bounds flush to tiles.
    var (wnone, hnone) = OverlayPanel.Measure(4, 2, 48, 6, 0, 0);
    Assert(wnone == 2 * 48 + 6 && hnone == 2 * 48 + 6, "no-padding mode flush");
});

Run("OverlaySettingsRowsRoundTripAndClamp", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"overlay-settings-{Guid.NewGuid():N}.json");
    try
    {
        File.WriteAllText(path, "{ \"rows\": 3, \"columns\": 2 }");
        Assert(OverlaySettings.Load(path).Rows == 3, "rows loaded");
        File.WriteAllText(path, "{ \"rows\": 99 }");
        Assert(OverlaySettings.Load(path).Rows == 12, "rows clamped high");
        File.WriteAllText(path, "{ \"rows\": -2 }");
        Assert(OverlaySettings.Load(path).Rows == 0, "rows clamped to auto");
        new OverlaySettings { Rows = 2 }.Save(path);
        Assert(OverlaySettings.Load(path).Rows == 2, "rows round-trips camelCase");
    }
    finally
    {
        File.Delete(path);
    }
});

Run("CatalogSelectionFollowsExactSlugOrder", () =>
{
    var mark = validatedSkill;
    var fury = inferredSkill with { Slug = "gravekeeper/loki-s-fury", Name = "Loki's Fury" };
    var selected = SkillCatalog.SelectInOrder(new[] { mark, fury }, new[] { fury.Slug });
    Assert(selected.Count == 1, "stale unselected skill excluded");
    Assert(selected[0].Slug == fury.Slug, "requested Fury is not replaced by Mark");
});

Run("LayoutPresetCellsSupportHorizontalPairsAndNumber3", () =>
{
    var horizontal = OverlayPanel.CellsFor(3, 2, 0, OverlayLayoutPreset.Horizontal, null);
    Assert(horizontal.Count == 3, "horizontal keeps every skill");
    Assert(horizontal.All(c => c.Y == 0) && horizontal[2].X == 2, "horizontal lays tiles in one row");

    var pairs = OverlayPanel.CellsFor(5, 9, 9, OverlayLayoutPreset.Pairs, null);
    Assert(pairs.Count == 5 && pairs[0].X == 0 && pairs[1].X == 1 && pairs[2].Y == 1,
        "pairs pack two per row in order");

    // Number-3 shape: top 2, center-right 1, bottom 2.
    var three = OverlayPanel.CellsFor(5, 9, 9, OverlayLayoutPreset.Number3, null);
    Assert(three.SequenceEqual(new[]
    {
        new OverlayCell(0, 0), new OverlayCell(1, 0), new OverlayCell(1, 1),
        new OverlayCell(0, 2), new OverlayCell(1, 2),
    }), "number-3 shape matches the requested three");

    // The panel hugs the shape: 2 columns wide, 3 rows tall.
    var (w3, h3) = OverlayPanel.Measure(three, 40, 4, 4);
    Assert(w3 == 2 * 40 + 4 + 8 && h3 == 3 * 40 + 2 * 4 + 8,
        $"number-3 panel hugs occupied cells, got {w3}x{h3}");

    // Overflow repeats the shape to the right, never drops skills.
    var six = OverlayPanel.CellsFor(6, 9, 9, OverlayLayoutPreset.Number3, null);
    Assert(six.Count == 6 && six[5] == new OverlayCell(3, 0), "pattern repeats side by side");

    // Custom positions drive exact per-skill placement; empty/invalid custom falls back to grid.
    var custom = OverlayPanel.CellsFor(2, 9, 9, OverlayLayoutPreset.Custom,
        new[] { new OverlayCell(2, 1), new OverlayCell(0, 0) });
    Assert(custom.SequenceEqual(new[] { new OverlayCell(2, 1), new OverlayCell(0, 0) }),
        "custom pattern is honored in order");
    var duplicate = OverlayPanel.CellsFor(3, 9, 9, OverlayLayoutPreset.Custom,
        new[] { new OverlayCell(1, 1), new OverlayCell(1, 1), new OverlayCell(9, 9) });
    Assert(duplicate.Count == 3 && duplicate.Distinct().Count() == 3,
        "duplicate custom cells are resolved to unique positions");
    var fallback = OverlayPanel.CellsFor(2, 2, 0, OverlayLayoutPreset.Custom, null);
    Assert(fallback.SequenceEqual(new[] { new OverlayCell(0, 0), new OverlayCell(1, 0) }),
        "custom with no pattern falls back to grid");
});

Run("LayoutPresetAndCustomCellsPersist", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"overlay-layout-{Guid.NewGuid():N}.json");
    try
    {
        var settings = new OverlaySettings
        {
            LayoutPreset = OverlayLayoutPreset.Number3,
            CustomCells = new List<OverlayCell> { new(0, 0), new(1, 1), new(2, 0) },
        };
        settings.Save(path);
        var loaded = OverlaySettings.Load(path);
        Assert(loaded.LayoutPreset == OverlayLayoutPreset.Number3, "preset survives round-trip");
        Assert(loaded.CustomCells.SequenceEqual(new[] { new OverlayCell(0, 0), new OverlayCell(1, 1), new OverlayCell(2, 0) }),
            "custom occupied cells survive round-trip");

        Assert(OverlayPanel.TryParsePattern("0,0;1,0;1,1;0,2;1,2", out var parsed) && parsed.Count == 5,
            "pattern text parses to cells");
        Assert(!OverlayPanel.TryParsePattern("nonsense", out _), "invalid pattern rejected");
        Assert(OverlayPanel.FormatPattern(parsed) == "0,0;1,0;1,1;0,2;1,2", "pattern formats back to text");
        Assert(OverlayPanel.ResolveCustom(3, new List<OverlayCell> { new(1, 1), new(1, 1), new(9, 9) }, 12)
            .Distinct().Count().Equals(3), "custom editor cells resolve without duplicates");
        Assert(OverlayPanel.Normalize(new List<OverlayCell> { new(3, 2), new(0, 1) })
            .SequenceEqual(new[] { new OverlayCell(3, 1), new OverlayCell(0, 0) }),
            "custom positions normalize by minimum row only");
    }
    finally
    {
        File.Delete(path);
    }
});

Run("OverlaySettingsSelectionOrderIsPersisted", () =>
{
    // Tile order comes from the slug list order, so reordering in the
    // configuration UI must survive a save/load round-trip.
    var path = Path.Combine(Path.GetTempPath(), $"overlay-settings-{Guid.NewGuid():N}.json");
    try
    {
        var settings = new OverlaySettings
        {
            SelectedSkillSlugs = new List<string> { "b/c", "a/b", "d/e" },
        };
        settings.Save(path);
        var loaded = OverlaySettings.Load(path);
        Assert(loaded.SelectedSkillSlugs.SequenceEqual(new[] { "b/c", "a/b", "d/e" }),
            "ordered slugs survive round-trip");
    }
    finally
    {
        File.Delete(path);
    }
});

Console.WriteLine();
Console.WriteLine(
    failures.Count == 0
        ? $"ALL {passed} TESTS PASSED"
        : $"{failures.Count} FAILED, {passed} PASSED");

return failures.Count == 0 ? 0 : 1;

// ---------- Synthetic memory ----------

/// <summary>Dense sparse 32-bit image for structural reader tests.</summary>
class SyntheticMemory : ITimerMemory
{
    private readonly Dictionary<uint, uint> _cells = new();
    private readonly Dictionary<uint, (uint first, uint rest)> _volatile = new();
    private uint _nextAddress = 0x0010_0000;
    private readonly Dictionary<uint, nuint> _keyToNode = new();

    /// <summary>Address of the synthetic CGameMode object written by BuildList.</summary>
    public nuint GameModeObject { get; set; }

    public void RegisterNode(uint key, nuint node) => _keyToNode[key] = node;

    public nuint? FindNodeByKey(uint key) =>
        _keyToNode.TryGetValue(key, out var node) ? node : null;

    public nuint Alloc()
    {
        var address = _nextAddress;
        _nextAddress += 0x10;
        for (uint offset = 0; offset < 0x10; offset += 4)
        {
            _cells[address + offset] = 0;
        }
        return address;
    }

    public void Write(nuint address, uint value) => _cells[(uint)address] = value;

    public void MakeCellVolatile(uint key, uint firstRead, uint laterReads)
    {
        var node = _keyToNode[key];
        _volatile[(uint)(node + 12)] = (firstRead, laterReads);
    }

    public bool TryReadUInt32(nuint address, out uint value)
    {
        var cell = (uint)address;
        if (_volatile.TryGetValue(cell, out var v))
        {
            value = v.first;
            _volatile[cell] = (v.rest, v.rest);
            return true;
        }

        if (_cells.TryGetValue(cell, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}
