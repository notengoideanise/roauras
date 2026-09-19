#!/usr/bin/env python3
"""Build the static skill catalog + icon assets from the extracted client dump.

Authoritative dump: <repo>/refuge-decrypted/ (already extracted; this tool never
opens the source GRFs). Sources:

  - skillid.lub      SKID name -> numeric client skill ID
  - skillinfolist.lub SKID name -> client SkillName
  - texture/À¯ÀúÀÎÅÍÆäÀÌ½º/item/<skid>.bmp   skill icon art (lowercase SKID name)
  - refuge-database-live/rtmdb-skillnames.json  user-facing class join

Priority for definitions and icons: extraupdates > prmbase > data.

Outputs:
  data/skill-catalog.json           catalog rows (slug, name, class, skid,
                                    runtimeKey, iconHandle, iconFile, status)
  data/skill-mapping-evidence.json  provenance + special-key policy
  data/icons/<SKID>.bmp             packaged icon art referenced by iconFile

The catalog carries NO cooldown-duration field: live client timer expiry ticks
remain the sole countdown authority.
"""
from __future__ import annotations

import argparse
import json
import re
import shutil
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DUMP = REPO / "refuge-decrypted"
RTMDB = REPO / "refuge-database-live" / "rtmdb-skillnames.json"
OUT_DIR = Path(__file__).resolve().parents[1] / "data"
ICONS_DIR = OUT_DIR / "icons"

# Host-validated runtime timer keys (controlled Windows recordings).
VALIDATED_KEYS = {
    78: "Loki's Mark",
    2326: "Alpha",
    2343: "Cursed Coffin",
}

TREE_PRIORITY = ("extraupdates", "prmbase", "data")

SKID_DIR = "luafiles514/lua files/skillinfoz"
ICON_DIRS = ("data/texture/À¯ÀúÀÎÅÍÆäÀÌ½º/item",)


def load_skid_ids() -> dict[str, int]:
    """SKID name -> numeric ID, newest tree wins."""
    ids: dict[str, int] = {}
    for tree in TREE_PRIORITY:
        path = DUMP / tree / "data" / SKID_DIR / "skillid.lub"
        if not path.exists():
            continue
        text = path.read_text(encoding="latin-1")
        for name, value in re.findall(r"([A-Z0-9_]+)\s*=\s*(\d+)", text):
            ids[name] = int(value)
    return ids


def load_skill_names() -> dict[str, str]:
    """SKID name -> client SkillName, newest tree wins."""
    names: dict[str, str] = {}
    for tree in TREE_PRIORITY:
        path = DUMP / tree / "data" / SKID_DIR / "skillinfolist.lub"
        if not path.exists():
            continue
        text = path.read_text(encoding="latin-1")
        for skid, body in re.findall(
            r"\[SKID\.([A-Z0-9_]+)\]\s*=\s*\{(.*?)\n\t\}", text, re.S
        ):
            match = re.search(r'SkillName\s*=\s*"((?:[^"\\]|\\.)*)"', body)
            if match:
                names[skid] = match.group(1)
    return names


def load_classes() -> dict[str, str]:
    """Lowercased client-facing name -> class from the captured database."""
    payload = json.loads(RTMDB.read_text(encoding="utf-8"))
    classes: dict[str, str] = {}
    slugs: dict[str, str] = {}
    for name, slug, class_name in payload["skills"]:
        key = name.lower()
        classes.setdefault(key, class_name)
        slugs.setdefault(key, slug)
    return classes, slugs


def icon_source(skid: str) -> Path | None:
    """First existing icon BMP for a SKID, by tree priority."""
    for tree in TREE_PRIORITY:
        for base in ICON_DIRS:
            candidate = DUMP / tree / base / f"{skid.lower()}.bmp"
            if candidate.exists():
                return candidate
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()

    ids = load_skid_ids()
    names = load_skill_names()
    classes, slugs = load_classes()

    skills: list[dict] = []
    icon_copied = 0
    missing_icons: list[str] = []
    ICONS_DIR.mkdir(parents=True, exist_ok=True)

    # Sort by numeric client ID for a stable, human-guessable order.
    def sort_key(skid: str):
        return ids.get(skid, 999_999)

    for skid in sorted(names, key=sort_key):
        client_name = names[skid].strip()
        key = client_name.lower()
        numeric_id = ids.get(skid)
        if numeric_id is None:
            status, evidence = "Unknown", "no numeric client ID in skillid.lub"
        elif numeric_id in VALIDATED_KEYS:
            status = "Validated"
            evidence = (
                f"timer key {numeric_id} host-validated by controlled recording "
                f"for '{VALIDATED_KEYS[numeric_id]}'"
            )
        else:
            status = "StaticInferred"
            evidence = (
                f"runtime timer key assumed equal to client skill ID {numeric_id} "
                f"(SKID {skid}); pattern supported by the three validated pairs, "
                "not individually live-correlated"
            )

        icon_file = f"{skid}.bmp"
        source_icon = icon_source(skid)
        if source_icon is not None:
            destination = ICONS_DIR / icon_file
            if not destination.exists() or destination.stat().st_size == 0:
                shutil.copyfile(source_icon, destination)
            icon_copied += 1
        else:
            icon_file = None
            missing_icons.append(skid)

        slug = slugs.get(key) or f"client/{skid.lower()}"
        skills.append(
            {
                "slug": slug,
                "name": client_name,
                "className": classes.get(key, ""),
                "skid": skid,
                "runtimeKey": numeric_id,
                "iconHandle": skid,
                "iconFile": icon_file,
                "mappingStatus": status,
                "evidence": evidence,
            }
        )

    slug_counts: dict[str, int] = {}
    for skill in skills:
        slug_counts[skill["slug"]] = slug_counts.get(skill["slug"], 0) + 1
    duplicate_slugs = sum(count - 1 for count in slug_counts.values() if count > 1)

    status_counts: dict[str, int] = {}
    for skill in skills:
        status_counts[skill["mappingStatus"]] = (
            status_counts.get(skill["mappingStatus"], 0) + 1
        )

    catalog = {
        "generatedAt": __import__("datetime").datetime.now(
            __import__("datetime").timezone.utc
        ).isoformat(),
        "sources": {
            "dump": "refuge-decrypted/",
            "skillIds": "luafiles514/lua files/skillinfoz/skillid.lub",
            "skillNames": "luafiles514/lua files/skillinfoz/skillinfolist.lub",
            "icons": "refuge-decrypted/<tree>/data/texture/*/item/<skid>.bmp",
            "classes": "refuge-database-live/rtmdb-skillnames.json",
            "note": (
                "Static catalog only; runtime cooldowns come from live client "
                "timer expiry ticks, never from database cooldown values."
            ),
        },
        "rowCount": len(skills),
        "duplicateSlugs": duplicate_slugs,
        "counts": {
            "mapping": status_counts,
            "iconsCopied": icon_copied,
            "iconsMissing": len(missing_icons),
        },
        "validatedKeys": {str(k): v for k, v in sorted(VALIDATED_KEYS.items())},
        "skills": skills,
    }

    evidence = {
        "generatedAt": catalog["generatedAt"],
        "method": (
            "Timer-key == client skill ID (SKID numeric value). Supported by three "
            "host-validated pairs (78 Loki's Mark, 2326 Alpha, 2343 Cursed Coffin); "
            "all other rows are StaticInferred and display NeedsValidation until a "
            "controlled observation confirms them. Never database-id guesswork."
        ),
        "specialKeyPolicy": (
            "No SKID is excluded: 9999 (FOLLOWER_NPC_RESET) and 10000 (GD_APPROVAL) are "
            "real client skill IDs and are cataloged like any other skill. The earlier "
            "'special recovery key 577' interpretation from the Gravekeeper probe is "
            "superseded: 577 is DA_RESET, a normal skill key. Timer keys outside the "
            "SKID space remain unassigned to skills."
        ),
        "validatedPairs": {str(k): v for k, v in sorted(VALIDATED_KEYS.items())},
        "iconCoverage": {
            "copied": icon_copied,
            "missing": len(missing_icons),
            "missingSkids": missing_icons[:64],
        },
    }

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    (OUT_DIR / "skill-catalog.json").write_text(
        json.dumps(catalog, indent=2, ensure_ascii=False), encoding="utf-8"
    )
    (OUT_DIR / "skill-mapping-evidence.json").write_text(
        json.dumps(evidence, indent=2, ensure_ascii=False), encoding="utf-8"
    )

    print(f"skills: {len(skills)}  icons: {icon_copied} copied, {len(missing_icons)} missing")
    print(f"mapping counts: {status_counts}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
