# Cooldown Overlay — Implementation Report

Date: 2026-09-15. Implements Tasks 1–6 of
`docs/superpowers/plans/2026-09-15-read-only-cooldown-overlay.md` per spec
`docs/superpowers/specs/2026-09-15-read-only-cooldown-overlay-design.md`.
Updated 2026-09-15 (review fix round 1): attach recovery, PRM window tracking,
game-mode chain validation, lifecycle tests, PRM_* icon classification,
configuration selection persistence, countdown formatting/label clipping.

## Review fix round 1 (this update)

1. **Attach recovery / revalidation** (`OverlayController.cs`): a vanished PID
   (throwing `Process.GetProcessById`) no longer wedges the controller — it is
   treated as exited and the controller detaches and rescans. Every poll
   boundary revalidates the attachment: process alive, executable path resolved
   and unchanged, and the exact digest still mapping to the attached profile.
   The file is re-hashed only when its identity (length + write time) changes;
   `AttachPolicy.ShouldDetach` (pure, in Core) makes the decision and a digest
   change forces detach + fresh exact-hash attach. The hash gate still runs
   before any process open or candidate read.
2. **PRM window tracking** (`OverlayController.FollowTargetWindow`,
   `Win32Window.FindMainWindow`): the overlay follows the attached PID's visible
   top-level window via `EnumWindows`/`GetWindowRect` plus the saved anchor
   offset; hides while the window is missing/minimized and when no process is
   attached. Skipped in configuration mode; drag with anchor save is supported
   (see 4). No input hooks — enumeration/read-only calls only.
3. **Game-mode chain validation** (`TimerReader.ValidateGameChain`): before any
   timer sample, the reader checks manager+0x58 == 1, mode = [manager+4], and
   [mode] == relocated CGameMode vtable — the same bounded chain as the
   host-validated Gravekeeper reader. Synthetic tests cover valid chain,
   manager-state rejection, and vtable-mismatch rejection.
4. **Lifecycle / decision tests**: new `AttachPolicy` (pure) tests cover
   keep/detach on process exit, digest change, and missing path/digest;
   `TryAttachRejectsUnknownHashBeforeOpen` proves unknown digests refuse attach
   before any process access; `NoStaleReadyAfterInvalidSample` proves a
   previously-cooling skill shows SamplingUnavailable (never ready) after an
   invalid sample.
5. **PRM_* classification + selection persistence**: `pit-boss/
   momentum-mastery` (icon `PRM_MOMENTUM_MASTERY`) is now `Custom` (catalog
   counts: Validated 3, StaticInferred 0, Custom 1, Unknown 507); the extractor
   classifies any PRM_* slug/name/icon handle as Custom. `ConfigurationForm`
   keeps selection in a slug-keyed checked set parallel to the list items, so
   skills hidden by the search filter survive Apply and duplicate labels cannot
   cross-match.
6. **Countdown formatting / clipping**: `CooldownFormat.Seconds` (Core, tested)
   keeps 100–119 s numeric; minutes format only from 120 s ("2m00"). The
   overlay reserves a 16 px label strip under the last row so skill names no
   longer clip.
7. **Compatibility regression**: `test_gk_timer_compatibility.py` gains
   `test_patch15_and_patch16_are_exact_aliases_of_patch14` (digest identity +
   bounded-report acceptance of Patch 16). No existing gate was weakened.

## What was built (original pass)

### Task 1 — Core project and domain models (DONE, tested)

`src/RefugeCooldownOverlay.Core` (net6.0, portable): `Domain.cs` with
`SkillDefinition`, `TimerSnapshot` (invalid samples carry an error, never a
"ready" implication), `SkillCooldownState`, and the `SkillMappingStatus` /
`CooldownDisplayState` enums from the plan interfaces.

### Task 2 — Skill catalog extractor and artifacts (DONE, tested)

`tools/build_skill_catalog.py` joins `rtmdb-skills.json` (511 rows) with
`rtmdb-skillnames.json`, records icon handles, and emits
`data/skill-catalog.json` + `data/skill-mapping-evidence.json`.

- 511 rows, 0 duplicate slugs. Counts: Validated 3, StaticInferred 0,
  Custom 0, Unknown 508.
- Validated keys (host-proven only): Loki's Mark → 78, Alpha → 2326,
  Cursed Coffin → 2343.
- Special keys 577 / 9999 / 10000 documented as non-skill/unresolved and
  never assigned to any skill.
- No cooldown-duration field exists anywhere in the catalog (test-enforced).
- StaticInferred tier exists in the model but this pass assigns none: the
  database slug/id space is not proven equal to the client runtime timer-key
  space, so guessing would display wrong cooldowns.

### Task 3 — Exact-build profile registry + bounded read-only reader (DONE, tested)

- `ProcessProfiles.cs`: exact SHA-256 registry. Legacy, Patch 13, and
  Patch 14 profile; Patch 14 profile display name carries the Patch 15/16
  aliases (verified byte-identical: `sha256sum` of PRM-patch14/15/16.exe all
  equal `205697800e6c…26755`). No version-string or file-size fallback.
- `MemoryReader.cs`: `ProcessTimerMemory` hashes the on-disk executable
  BEFORE `OpenProcess(0x1010)`; resolves the main module base via
  Toolhelp snapshot; read-only P/Invoke only.
- `TimerReader.cs`: bounded linked-list sampler (sentinel `0x1107A34`
  relocated from module base; nodes +0 next / +4 prev / +8 key / +0xC expiry;
  128-node cap; backlink, duplicate-key, header, and stability re-reads;
  injectable clock for offline tests).

### Task 4 — Expiry-only projector (DONE, tested)

`CooldownProjector.cs`: `Remaining(expiry, tick)` with the same signed
modular semantics as the host-validated AHK reader. State rules exactly per
plan: validated+timer → Cooldown; validated+absent → Ready; invalid sample →
SamplingUnavailable; inferred → NeedsValidation always; custom/unknown →
MappingUnavailable. Database durations cannot reach the display (no such
field exists in the pipeline).

### Tasks 5–6 — WinForms shell, controller, packaging (SOURCE DONE; build verified via cross-compile; runtime UNVERIFIED on Linux)

- `RefugeCooldownOverlay.App` (net6.0-windows): `OverlayForm` (layered,
  topmost, non-activating, click-through in display mode, dim + countdown
  rendering, explicit `?M`/`?V`/`--` placeholders), `ConfigurationForm`
  (searchable skill selection with mapping status, columns/icon size/poll/
  opacity/click-through), `StatusForm`, `OverlayController` (attach by
  process name → hash gate → poll → project → render; detach on process
  exit; clamped poll 25–1000 ms), `OverlaySettings` (JSON, clamped, slugs
  not raw keys), `Win32Window` (ex-style helpers only; no input hooks).
- `RefugeCooldownOverlay.sln`, `data/overlay-settings.json` template
  (defaults to the three validated Gravekeeper skills), `package.ps1`
  (self-contained win-x64 publish + data copy).
- `README.md` documents the safety contract, mapping honesty, and build
  steps.

## Verification (after fix round 1)

| Command | Result |
|---|---|
| `dotnet run --project cooldown-overlay/tests/RefugeCooldownOverlay.Tests/…` | ALL 37 TESTS PASSED (was 27) |
| `python3 -m unittest test_gk_timer_compatibility` | OK (7 tests, incl. new Patch 15/16 alias test) |
| `dotnet build RefugeCooldownOverlay.sln -c Release -p:EnableWindowsTargeting=true` | Build succeeded, 0 errors, 0 warnings |
| Forbidden-API scan (WriteProcessMemory/CreateRemoteThread/VirtualAllocEx/SetWindowsHookEx/SendInput/keybd_event/mouse_event/WinSock/HttpClient/Socket/CreateFileMapping) | NO matches in src/tools/package.ps1 (only doc-comment mentions of the word "hook") |

Test coverage (37 offline tests): expiry-only arithmetic incl. wraparound;
valid/empty list; count mismatch; over-cap count; duplicate key; concurrent
mutation; backlink mismatch; unreadable header; manager-state and
vtable-mismatch gating; attach-policy keep/detach matrix; unknown-hash attach
refusal; no-stale-ready after invalid sample; exact-hash acceptance +
near-match rejection; patch16-alias acceptance; projector state matrix incl.
inferred-never-authoritative; countdown formatting (100–119 s numeric);
catalog invariants (511 rows, no cooldown field, validated keys 78/2326/2343,
special keys unassigned, PRM_ icons Custom); layout
placement/clamping/stability.

## Unverified Windows-only steps (require a Windows host)

1. Live WinForms behavior: transparency, click-through, drag-to-position,
   PRM window tracking (`EnumWindows`/`GetWindowRect` path).
2. Real `PRM-patch16.exe` attach + live timer sampling vs. the game,
   including the manager/game-mode chain reads against live memory.
3. `package.ps1` publish run.
4. Controlled observation to validate further timer keys (see upgrade policy
   in `data/skill-mapping-evidence.json`).

## Blockers

None blocking handoff. Linux host cannot run WinForms; source cross-compiles
clean and all portable logic (including the pure attach-policy seam and the
synthetic manager-chain fixtures) is tested. The poll-boundary digest
revalidation hashes the executable only when length/write-time change, not on
every 25 ms poll — noted as a deliberate ceiling; if a patcher rewrites the
file in place while preserving mtime/length, the digest check would be
deferred until the next identity change (no known patcher does this).

## Safety notes

- No input generation, writes, injection, hooks, packet access, or network
  calls anywhere in the new code. Window tracking uses enumeration and
  rectangle reads only.
- Hash gate executes before process open; near-match digests rejected;
  attachment revalidated at every poll boundary via the pure AttachPolicy seam.
- Invalid samples never render as ready; inferred/unknown mappings never
  render as ready; the manager/game-mode chain gates every timer sample.
- No git commits made (repo has no git metadata).

## Final-review fix round (2026-09-15)

Release blockers and P2s from the independent review, applied and verified:

1. **Module identity by path, not name (blocker).** `MemoryReader.cs` no longer
   requires the main module to be named `prm.exe`. After the exact-hash gate
   (unchanged: hash before `OpenProcess`), the first module's full executable
   path is compared to the hashed executable path via the new portable
   `ModuleIdentity.MatchesExecutablePath` (case-insensitive, slash-normalized).
   `PRM.exe`, `PRM-patch16.exe`, and future patch names are accepted; a path
   mismatch refuses to read.
2. **Settings JSON policy (blocker).** `OverlaySettings` moved to
   `RefugeCooldownOverlay.Core` (pure System.Text.Json, no WinForms) with
   explicit options: `LoadOptions` (`PropertyNameCaseInsensitive=true`) so the
   shipped camelCase `data/overlay-settings.json` template and user edits load
   correctly, and `SaveOptions` (`CamelCase`, indented) so the on-disk format is
   intentional camelCase. Clamp behavior unchanged and test-covered.
3. **P2: live poll interval.** `OverlayController.ApplyPollInterval(ms)` (same
   25–1000 ms clamp), wired from the configuration apply callback.
4. **P2: label overdraw.** `OverlayForm.OnPaint` now paints in two passes: all
   cell fills/borders first, then labels, so a following row's fill can never
   overwrite an earlier row's label.
5. **P2: multi-instance visibility.** Candidate processes are picked
   deterministically (lowest PID); when more than one PRM process exists, the
   status line reports the count and chosen PID, and every sample line includes
   the attached PID.

### Verification (this round)

| Command | Result |
|---|---|
| `dotnet build cooldown-overlay/RefugeCooldownOverlay.sln -c Release -p:EnableWindowsTargeting=true` | Build succeeded, 0 warnings, 0 errors |
| `dotnet run --project cooldown-overlay/tests/...` | ALL 42 TESTS PASSED (incl. 5 new: ModuleIdentity ×2, OverlaySettings ×3) |
| `python3 -m unittest test_gk_timer_compatibility.py` | OK (7 tests) |
| `python3 -m unittest test_gk_memory_policy.py` | OK (16 tests) |
| Forbidden-API scan over `src/` + `tools/` | No matches |

New/changed files this round:
`src/RefugeCooldownOverlay.Core/MemoryReader.cs`,
`src/RefugeCooldownOverlay.Core/OverlaySettings.cs` (moved from App),
`src/RefugeCooldownOverlay.App/OverlayController.cs`,
`src/RefugeCooldownOverlay.App/OverlayForm.cs`,
`src/RefugeCooldownOverlay.App/Program.cs`,
`tests/RefugeCooldownOverlay.Tests/Program.cs`.

## Discovery fix round (2026-09-15, supervisor-authorized inline)

**Blocker:** default `processName="PRM"` used `Process.GetProcessesByName("PRM")`,
so the current client (`PRM-patch16.exe`) was never discovered even though the
exact-hash/path attach path supported it.

**Fix:** new portable `RefugeCooldownOverlay.Core/ProcessDiscovery.cs`:

- Default selection (`PRM`, empty, or unset) accepts `PRM.exe` and any
  `PRM-patch<suffix>` executable; a bare `PRM-patch` prefix and look-alikes such
  as `PRM-spoof` / `PRMPatch16` are rejected.
- An explicit non-default `processName` is matched exactly (case-insensitive),
  so a specific patch or a non-PRM test process can be pinned.
- `OverlayController.EnsureAttached` uses the matcher when detached (default:
  `Process.GetProcesses()` filtered by the matcher; explicit:
  `GetProcessesByName`), keeping lowest-PID pick and multi-instance status.
- Trust boundary unchanged: the exact SHA-256 profile gate in
  `ProcessTimerMemory.TryAttach` still runs BEFORE `OpenProcess`; discovery is
  name-based convenience only.

### Verification (this round)

| Command | Result |
|---|---|
| `dotnet build cooldown-overlay/RefugeCooldownOverlay.sln -c Release -p:EnableWindowsTargeting=true` | Build succeeded, 0 warnings, 0 errors |
| `dotnet run --project cooldown-overlay/tests/...` | ALL 45 TESTS PASSED (incl. 3 new discovery tests) |
| `python3 -m unittest test_gk_timer_compatibility.py` | OK (7 tests) |
| `python3 -m unittest test_gk_memory_policy.py` | OK (16 tests) |
| Forbidden-API scan over `src/` + `tools/` | No matches |

Changed files this round:
`src/RefugeCooldownOverlay.Core/ProcessDiscovery.cs` (new),
`src/RefugeCooldownOverlay.App/OverlayController.cs`,
`tests/RefugeCooldownOverlay.Tests/Program.cs`, `README.md`, this report.

## Packaging (2026-09-15)

`package.ps1` rewritten, modeled on `trade-watch/package.ps1` (staging +
verification + atomic replace), minimal:

- Publishes `RefugeCooldownOverlay.App` self-contained single-file win-x64
  (Linux-safe: `-p:EnableWindowsTargeting=true`; deterministic PE subsystem
  rewrite console→GUI, same NETSDK1074 workaround as trade-watch; no-op on a
  native Windows build).
- Stages the publish tree + `skill-catalog.json` + `skill-mapping-evidence.json`
  + `overlay-settings.json` + `README.md` + `BUILD.txt`.
- Verification: x64 PE + Windows GUI subsystem on the apphost, no
  source/obj/build artifacts in the package, required-file presence,
  publish/staging hash equality, deterministic ZIP entry-set and per-file hash
  equality against the staged tree.
- Atomic replace of `transfer/refuge-cooldown-overlay-win-x64.zip` with previous
  archive backed up to `transfer/archive/<name>-previous-<hash>.zip` +
  sidecar; `.zip.sha256` written.
- No requestedExecutionLevel / admin forcing; `BUILD.txt` documents elevation
  must match PRM and binding is selected-PID discovery from PRM/PRM-patch*
  candidates through the exact SHA-256 gate.

Output this run:
`transfer/refuge-cooldown-overlay-win-x64.zip`
sha256 `3b39f2bd0714cf0afcee49364c8b03734ba56a980875644e8e65580d5e15539e`
sidecar verified with `sha256sum -c`. 8 files, 66,096,432 bytes ZIP;
`RefugeCooldownOverlay.App.exe` = 153,342,793 bytes self-contained.
Determinism: re-running with `-SkipPublish` reproduces the identical archive
byte-for-byte; the initial hash difference traced to a README edit between runs,
not packaging nondeterminism. The prior ZIP (73d2fdbd…) was preserved by the
script's own backup path under `transfer/archive/`.

Post-packaging re-verification: 45/45 overlay tests, 23/23 compatibility +
memory-policy tests, forbidden-API scan clean (`SCAN-CLEAN`).

## Windows crash fix + dark theme round (2026-09-15)

User-reported crash: `System.ArgumentException: Parameter is not valid` at
`OverlayForm.OnPaint` → `Graphics.DrawString`. Root cause: `using var textBrush =
Brushes.White;` disposed GDI+'s shared cached white brush at the end of the first
paint; every later paint then drew with a disposed brush. Fix: the overlay now
owns every brush/pen it creates (all `using`-disposed instances constructed from
the theme palette); shared `System.Drawing` brushes are never used.

Also in this round:

1. **Guaranteed click-through.** `OverlayForm.WndProc` answers every
   `WM_NCHITTEST` with `HTTRANSPARENT` in display mode (pure seam:
   `Core.ClickThroughPolicy.ShouldPassThrough`). `WS_EX_TRANSPARENT`,
   `WS_EX_NOACTIVATE`, `WS_EX_LAYERED`, `WS_EX_TOOLWINDOW` are preserved.
   Configuration mode disables pass-through for dragging and the existing
   close/apply path restores display-mode click-through. Game clicks and focus
   can never be stolen by the overlay in display mode.
2. **Whole-app dark theme.** New `Core/OverlayTheme.cs` palette (dark surfaces,
   muted light text, never pure white/black-key collisions) applied via
   `App/DarkTheme.cs` to `OverlayForm`, `ConfigurationForm` (dialog, search,
   CheckedListBox, numeric/track controls, flat dark buttons), `StatusForm`,
   and the tray `ContextMenuStrip` (dark `ProfessionalColorTable` renderer).
3. **Tests.** New offline tests: `ClickThroughPolicyPassesThroughOnlyInDisplayMode`,
   `ThemePaletteBackgroundsAreDark` (dark + never the transparency key; borders
   separately bounded), `ThemePaletteTextIsMutedLightNotWhite`.

### Verification (this round)

| Command | Result |
|---|---|
| `dotnet run --project cooldown-overlay/tests/...` | ALL 48 TESTS PASSED |
| `dotnet build ... -p:EnableWindowsTargeting=true` | 0 warnings, 0 errors |
| `python3 -m unittest test_gk_timer_compatibility.py test_gk_memory_policy.py` | OK (23 tests) |
| Forbidden-API scan over `src/` + `tools/` | No matches |

Changed files: `Core/OverlayTheme.cs` (new), `Core` tests `Program.cs`,
`App/OverlayForm.cs`, `App/DarkTheme.cs` (new), `App/StatusForm.cs`,
`App/ConfigurationForm.cs`, `App/Program.cs`, `README.md`, `BUILD.txt`.

Windows-only limit: WinForms rendering/click-through was verified by cross-build
and offline tests only; the crash path (second paint after brush disposal) is
structurally removed but final confirmation is the Windows smoke test.

## Dump-based rebuild (2026-09-16, worker resumed)

Implemented against /home/kali/Desktop/refuge/Refuge/refuge-decrypted/ (no GRF access):

- tools/build_skill_catalog.py rewritten: skillid.lub + skillinfolist.lub join,
  rtmdb class join, icon copy from dump texture trees (extraupdates > prmbase > data).
  Output: 1,449 skills; 1,168 icons packaged; 281 missing (NPC/mercenary skills
  with no client art, explicitly counted in evidence iconCoverage).
- Validated pairs preserved (78/2326/2343); all other SKIDs StaticInferred
  (NeedsValidation UI). 9999/10000 are real SKIDs (FOLLOWER_NPC_RESET,
  GD_APPROVAL) - the old special-key exclusion is obsolete.
- Core: OverlayLayout rows param, OverlaySettings.Rows, AnchorMath, SkillMonogram,
  OverlayLog (rolling 1 MiB + backup, never throws), SkillDefinition.IconFile,
  SkillCatalog iconFile/skid loading.
- App: icon rendering with cooldown wash + centered countdown and badges,
  no name labels; StatusForm removed (fixes ObjectDisposedException on
  Configure-reopen); config created on demand/disposed on close; tray
  "Open Log Folder"; global UI exception logging; PRM-foreground-only
  visibility; WM_MOUSEACTIVATE=MA_NOACTIVATE; ordered selection with Up/Down.
- package.ps1 now stages data/icons into the ZIP.

Verification: 55/55 overlay tests, 23/23 compatibility/memory-policy tests,
zero-warning cross-build (EnableWindowsTargeting), forbidden-API scan clean.
ZIP: transfer/refuge-cooldown-overlay-win-x64.zip
SHA-256: 13c9f3d240b088123b9ee408efafdea402992cc6d8111cad669742c24ef06741
Windows-only residuals: live click-through/focus behavior, icon scaling quality,
foreground-follow timing, log rotation under real usage.
