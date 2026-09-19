#!/usr/bin/env python3
"""Extract skill icon BMPs from the Refuge GRF archives.

Standalone GRF reader (no external grf lib): implements the standard GRF file
table layout plus the cps.dll runtime-key transform already documented in
decrypt_refuge_grf.js. Reads only the entries it needs; never writes to the
game directory.

Sources are searched newest-first (extraupdates > prmbase > data.grf); the
first archive containing an icon wins. Output: one <iconHandle>.bmp per
catalog icon handle under data/icons/, plus a manifest JSON and a missing list.
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
import zlib
from pathlib import Path

GRF_HEADER_SIZE = 46


def find_runtime_key(cps_path: Path, exe_name: str = "PRM.exe") -> bytes:
    """Recover the GRF stream key from cps.dll (same algorithm as
    decrypt_refuge_grf.js findRuntimeKey)."""
    cps = cps_path.read_bytes()
    exe = exe_name.encode("latin-1")
    inverted = bytes(b ^ 0xFF for b in exe)
    name_offset = cps.find(inverted)
    if name_offset < 264:
        raise RuntimeError("Could not locate the GRF key container in cps.dll")
    structure_offset = name_offset - 264
    masked_key_offset = structure_offset + 4
    (mask,) = struct.unpack_from("<I", cps, masked_key_offset)
    key = bytearray(256)
    for index in range(256):
        key[index] = cps[masked_key_offset + 4 + index] ^ (mask & 0xFF)
        mask = (mask * 47) & 0xFFFFFFFF
    return bytes(key)


def transform(data: bytearray, key: bytes, decompressed_size: int) -> None:
    state = bytearray(key)
    length = len(state)
    first = decompressed_size % length
    second = 0
    for offset in range(len(data)):
        first = (first + 1) % length
        second = (second + state[first]) % length
        state[first], state[second] = state[second], state[first]
        data[offset] ^= state[(state[first] + state[second]) % length]


def is_zlib(data: bytes) -> bool:
    return len(data) >= 2 and (data[0] & 0x0F) == 8 and (((data[0] << 8) | data[1]) % 31) == 0


class GrfReader:
    def __init__(self, path: Path, key: bytes):
        self.path = path
        self.key = key
        self._fd = None
        self.entries = {}  # basename-upper -> (offset, aligned_size, real_size, type)
        self._open()

    def _open(self) -> None:
        import mmap

        self._fd = open(self.path, "rb")
        header = self._fd.read(GRF_HEADER_SIZE)
        if header[:15] != b"Master of Magic":
            raise RuntimeError(f"{self.path}: not a GRF archive")
        (version,) = struct.unpack_from("<I", header, 0x1F)
        (table_comp_size, table_real_size) = struct.unpack_from("<II", header, 0x23)
        table_raw = bytearray(self._fd.read(table_comp_size))
        if not is_zlib(bytes(table_raw[:2])):
            # Encrypted archives scramble the file table with the same stream.
            transform(table_raw, self.key, table_real_size)
        table = zlib.decompress(bytes(table_raw), bufsize=table_real_size + 64)
        self._fd.close()
        self._fd = open(self.path, "rb")
        self._mm = mmap.mmap(self._fd.fileno(), 0, access=mmap.ACCESS_READ)

        pos = 0
        total = len(table)
        while pos + 1 < total:
            (name_len,) = struct.unpack_from("<H", table, pos)
            pos += 2
            if name_len == 0 or pos + name_len + 13 > total:
                break
            raw_name = table[pos : pos + name_len]
            pos += name_len
            file_offset, aligned, real_size, ftype = struct.unpack_from(
                "<IIIB", table, pos
            )
            pos += 13
            try:
                name = raw_name.decode("cp949")
            except UnicodeDecodeError:
                name = raw_name.decode("latin-1")
            name = name.replace("\\", "/")
            if ftype & 0x01:  # file entries only
                base = name.rsplit("/", 1)[-1].upper()
                self.entries[base] = (
                    file_offset + GRF_HEADER_SIZE,
                    aligned,
                    real_size,
                    ftype,
                )
        self.version = version

    def read_entry(self, base_upper: str) -> bytes | None:
        entry = self.entries.get(base_upper)
        if entry is None:
            return None
        offset, aligned, real_size, ftype = entry
        packed = bytearray(self._mm[offset : offset + aligned])
        if not is_zlib(bytes(packed[:2])):
            transform(packed, self.key, real_size)
        try:
            out = zlib.decompress(bytes(packed), bufsize=real_size + 64)
        except zlib.error:
            return None
        if len(out) != real_size:
            return None
        return out

    def has(self, base_upper: str) -> bool:
        return base_upper in self.entries

    def close(self) -> None:
        try:
            self._mm.close()
        finally:
            self._fd.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", required=True, help="skill-catalog.json path")
    parser.add_argument("--game-root", default=".", help="directory containing cps.dll and GRFs")
    parser.add_argument(
        "--archives",
        nargs="+",
        default=["extraupdates.grf", "prmbase.grf", "data.grf"],
        help="GRFs, searched newest-first",
    )
    parser.add_argument("--output", required=True, help="icon output directory")
    args = parser.parse_args()

    root = Path(args.game_root)
    out_dir = Path(args.output)
    out_dir.mkdir(parents=True, exist_ok=True)

    catalog = json.loads(Path(args.catalog).read_text(encoding="utf-8"))
    handles = sorted({s["iconHandle"] for s in catalog["skills"] if s.get("iconHandle")})
    print(f"catalog icon handles: {len(handles)}")

    key = find_runtime_key(root / "cps.dll")
    readers = []
    for archive in args.archives:
        path = root / archive
        if path.exists():
            reader = GrfReader(path, key)
            print(f"{archive}: {len(reader.entries)} file entries")
            readers.append(reader)
        else:
            print(f"{archive}: missing, skipped")
    if not readers:
        print("ERROR: no GRF archives found", file=sys.stderr)
        return 1

    extracted, missing = 0, []
    for handle in handles:
        target = out_dir / f"{handle}.bmp"
        if target.exists():
            extracted += 1
            continue
        data = None
        for reader in readers:
            if reader.has(handle.upper()):
                data = reader.read_entry(handle.upper())
                if data:
                    break
        if data is None:
            missing.append(handle)
            continue
        target.write_bytes(data)
        extracted += 1

    manifest = {
        "sourceArchives": args.archives,
        "catalogHandles": len(handles),
        "extracted": extracted,
        "missing": missing,
    }
    (out_dir / "icon-manifest.json").write_text(
        json.dumps(manifest, indent=2), encoding="utf-8"
    )
    for reader in readers:
        reader.close()

    print(f"extracted: {extracted}")
    print(f"missing: {len(missing)}")
    if missing[:20]:
        print("first missing:", ", ".join(missing[:20]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
