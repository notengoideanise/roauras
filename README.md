# Refuge Cooldown Overlay (read-only)

Standalone Windows overlay showing selected Refuge/PRM skill cooldowns from the
client's **live timer list**. Countdowns are computed from the client's own expiry
tick and `winmm!timeGetTime`, so equipped cooldown reduction shows immediately.
No database/base cooldown value ever drives the display.

## Safety contract

- **Read-only process access** (`OpenProcess` query+read, `ReadProcessMemory`). No
  writes, injection, hooks, packet access, input generation, or networking.
- **Exact SHA-256 build gate** before any process open or offset read
  (legacy / patch13 / patch14 profiles; Patch 15/16 are byte-identical to Patch 14;
  Patch 18/19/20 require their own exact hashes and reviewed timer regions).
- Unknown or near-match builds are rejected **before** the process is opened.
- **Discovery:** default `processName` ("PRM") matches `PRM.exe` and
  `PRM-patch*.exe`; explicit `processName` matches exactly. Lowest PID wins;
  every candidate still passes the hash gate.

## Mapping honesty

Catalog built from the extracted client dump (`refuge-decrypted/`): 1,449 skills
with real client SKID numeric IDs and 1,168 packaged icons (NPC/mercenary skills
without client art show a dark monogram tile).

- `Validated` (3): Loki's Mark → 78, Alpha → 2326, Cursed Coffin → 2343. Live
  countdowns from the client's own timer nodes.
- `StaticInferred` (1,446): runtime timer key assumed equal to the client skill ID
  (pattern proven by the three validated pairs). They render a subtle "?" badge
  until a controlled observation validates them — never a fabricated cooldown.

## Using the overlay

- Tray icon → **Configure…**: searchable skill list (all 1,449 skills, mapping
  status shown), tile **order** list with Up/Down, columns, rows (0 = auto),
  icon size, spacing, opacity, poll interval, click-through toggle.
- **Position:** with Configure open, drag the overlay itself; the anchor is saved
  relative to the PRM window's top-left, so it follows the game window.
- The overlay is visible **only while PRM is the foreground window**; Alt-Tab or
  minimizing PRM hides it. In display mode it is fully click-through and cannot
  take focus (`HTTRANSPARENT` + `MA_NOACTIVATE`).
- Tray → **Open Log Folder**: `overlay.log` (rolling, 1 MiB × 2) records startup,
  attach/detach, profile/hash, visibility transitions, sample validity, settings
  changes, and exceptions. No memory dumps, credentials, or input data.

## Build

```sh
dotnet run --project cooldown-overlay/tests/RefugeCooldownOverlay.Tests/RefugeCooldownOverlay.Tests.csproj
```

Regenerate catalog/icons from the dump (already checked in):

```sh
python3 cooldown-overlay/tools/build_skill_catalog.py
```

Package (Windows or cross-publish with pwsh + .NET SDK):

```powershell
pwsh -File cooldown-overlay/package.ps1
```

Output: `cooldown-overlay/transfer/roauras-win-x64.zip` + `.sha256`
sidecar (executable, updater, catalog, 1,168 icons, and settings template). Run
`RoAurasUpdater.exe` from installed folder to check configured GitHub manifest,
apply verified package, then launch RoAuras. Offline/update failures fall back to
installed app.

## Limitations

- Timer presence is client evidence only; server acceptance/damage is never claimed.
- Icon art coverage is the client's own: skills without art (NPC/mercenary) fall
  back to monogram tiles, counted in `data/skill-mapping-evidence.json`.
- Run with the same elevation as PRM; never bypass security software.
