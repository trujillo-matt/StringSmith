#!/usr/bin/env python3
"""Inspect and structurally verify Rocksmith 2014 PSARC packages.

This deliberately shares no code with the writer. The container is parsed straight
from the on-disk format and the TOC is decrypted with the openssl CLI, so a package
that only round-trips because the reader and the writer agree with each other will
still be caught here.

Usage:
    scripts/inspect-psarc.py [-v] <package.psarc> [more.psarc ...]

With more than one package it also reports identity collisions between them, which
is what a duplicate DLC key or a repeated PersistentID looks like from the game's
side. Exit status is non-zero if any problem was found.

Requires only Python 3.8+ and the openssl CLI (both present on stock macOS).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import zlib
from collections import defaultdict

# src/Rocksmith2014.PSARC/Cryptography.fs
PSARC_KEY = bytes.fromhex("C53DB23870A1A2F71CAE64061FDD0E1157309DC85204D4C5BFDF25090DF2572C")
HEADER_LENGTH = 32
TOC_ENTRY_SIZE = 30

# The attributes a shipped Rocksmith 2014 header manifest carries. Taken from a package
# built by Rocksmith2014.NET's own integration-test project, which is the reference this
# project's packaging path runs through. Vocal entries carry a much smaller record: the
# game gets their song-level data from the instrumental entries.
INSTRUMENTAL_NAMES = {"Lead", "Rhythm", "Bass", "Combo"}

REQUIRED_INSTRUMENTAL_ATTRS = {
    "AlbumArt", "AlbumName", "AlbumNameSort", "ArrangementName", "ArtistName",
    "ArtistNameSort", "CentOffset", "DLC", "DLCKey", "ManifestUrn", "MasterID_RDV",
    "PersistentID", "RouteMask", "SKU", "Shipping", "SongDifficulty", "SongKey",
    "SongLength", "SongName", "SongNameSort", "SongYear", "Tuning",
}

REQUIRED_VOCAL_ATTRS = {
    "ArrangementName", "DLC", "DLCKey", "ManifestUrn", "MasterID_RDV",
    "PersistentID", "SKU", "Shipping", "SongKey",
}


class Problem(Exception):
    pass


def aes256_cfb_decrypt(key: bytes, iv: bytes, data: bytes) -> bytes:
    """Decrypt with the openssl CLI so no AES implementation is shared with the writer."""
    padded = data + b"\0" * (-len(data) % 16)
    fd, path = tempfile.mkstemp()
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(padded)
        return subprocess.run(
            ["openssl", "enc", "-d", "-aes-256-cfb", "-nopad",
             "-K", key.hex(), "-iv", iv.hex(), "-in", path],
            capture_output=True, check=True).stdout
    except FileNotFoundError:
        raise Problem("the openssl CLI is required but was not found on PATH")
    except subprocess.CalledProcessError as e:
        raise Problem(f"openssl failed to decrypt the table of contents: {e.stderr.decode(errors='replace').strip()}")
    finally:
        if os.path.exists(path):
            os.unlink(path)


class Psarc:
    def __init__(self, path: str):
        self.path = path
        self.problems: list[str] = []
        self.raw = open(path, "rb").read()
        self.names: list[str] = []
        self.files: dict[str, bytes] = {}
        self._parse()

    def bad(self, msg: str) -> None:
        self.problems.append(msg)

    # -- container ---------------------------------------------------------

    def _parse(self) -> None:
        raw, total = self.raw, len(self.raw)
        if total < HEADER_LENGTH:
            raise Problem(f"file is only {total} bytes, shorter than a {HEADER_LENGTH}-byte header")
        if raw[0:4] != b"PSAR":
            raise Problem(f"not a PSARC: magic is {raw[0:4]!r}, expected b'PSAR'")
        if raw[8:12] != b"zlib":
            raise Problem(f"unsupported compression method {raw[8:12]!r}")

        u32 = lambda o: int.from_bytes(raw[o:o + 4], "big")
        self.version = (int.from_bytes(raw[4:6], "big"), int.from_bytes(raw[6:8], "big"))
        self.toc_length = u32(12)
        self.entry_size = u32(16)
        self.entry_count = u32(20)
        self.block_alloc = u32(24)
        self.flags = u32(28)

        if self.entry_size != TOC_ENTRY_SIZE:
            self.bad(f"ToCEntrySize is {self.entry_size}, expected {TOC_ENTRY_SIZE}")
        if self.entry_count == 0:
            raise Problem("ToCEntryCount is 0: the archive declares no entries at all")
        if self.entry_count >= 2 ** 31:
            raise Problem(
                f"ToCEntryCount is {self.entry_count}, which does not fit in a signed 32-bit "
                "integer. The header is corrupt; a reader that allocates a list of this size "
                "throws 'Non-negative number required. (Parameter capacity)'")
        if self.toc_length < HEADER_LENGTH:
            raise Problem(f"ToCLength {self.toc_length} is smaller than the {HEADER_LENGTH}-byte header")
        if self.toc_length > total:
            raise Problem(f"ToCLength {self.toc_length} runs past the end of the {total}-byte file")
        if self.block_alloc not in (1 << 16, 1 << 24, 1 << 32):
            self.bad(f"BlockSizeAlloc {self.block_alloc} is not a supported block size")

        self.ztype = {1 << 16: 2, 1 << 24: 3}.get(self.block_alloc, 4)
        toc_bytes = self.entry_count * self.entry_size
        table_bytes = self.toc_length - HEADER_LENGTH - toc_bytes
        if table_bytes < 0:
            raise Problem(
                f"ToCLength {self.toc_length} leaves {table_bytes} bytes for the block size "
                f"table after {self.entry_count} entries: the header is internally inconsistent")
        if table_bytes % self.ztype:
            self.bad(f"block size table is {table_bytes} bytes, not a multiple of zType {self.ztype}")

        body = raw[HEADER_LENGTH:self.toc_length]
        if self.flags == 4:
            body = aes256_cfb_decrypt(PSARC_KEY, b"\0" * 16, body)[:self.toc_length - HEADER_LENGTH]

        self.entries = []
        for i in range(self.entry_count):
            o = i * TOC_ENTRY_SIZE
            self.entries.append({
                "i": i,
                "md5": body[o:o + 16],
                "zbegin": int.from_bytes(body[o + 16:o + 20], "big"),
                "length": int.from_bytes(body[o + 20:o + 25], "big"),
                "offset": int.from_bytes(body[o + 25:o + 30], "big"),
            })
        table = body[toc_bytes:]
        self.blocks = [int.from_bytes(table[i * self.ztype:(i + 1) * self.ztype], "big")
                       for i in range(table_bytes // self.ztype)]

        if self.entries[0]["md5"] != b"\0" * 16:
            self.bad(f"the first entry's name digest is {self.entries[0]['md5'].hex()}; the nameless "
                     "manifest entry must hash to 16 zero bytes")

        manifest, _ = self._read(self.entries[0])
        try:
            self.names = manifest.decode("ascii").split("\n")
        except UnicodeDecodeError:
            raise Problem("the manifest entry did not decode as ASCII: the table of contents is corrupt")
        if len(self.names) != self.entry_count - 1:
            self.bad(f"the manifest lists {len(self.names)} names but the table of contents has "
                     f"{self.entry_count - 1} named entries")

        for entry, name in zip(self.entries[1:], self.names):
            if hashlib.md5(name.encode("ascii")).digest() != entry["md5"]:
                self.bad(f"name digest does not match {name!r}")

        self._check_layout(total)

    def _read(self, entry) -> tuple[bytes, int]:
        """Inflate one entry. A zero block size means a full, uncompressed cluster."""
        out = bytearray()
        z = entry["zbegin"]
        pos = entry["offset"]
        while len(out) < entry["length"]:
            if z >= len(self.blocks):
                self.bad(f"entry {entry['i']} ran past the end of the block size table at index {z}")
                break
            size = self.blocks[z] or self.block_alloc
            chunk = self.raw[pos:pos + size]
            if len(chunk) != size:
                self.bad(f"entry {entry['i']} block {z} is truncated: wanted {size} bytes at "
                         f"offset {pos}, the file has {len(chunk)}")
                break
            if chunk[:2] == b"\x78\xda":
                try:
                    out += zlib.decompress(chunk)
                except zlib.error:
                    out += chunk  # wem data that happens to start with zlib's magic
            else:
                out += chunk
            pos += size
            z += 1
        return bytes(out), z - entry["zbegin"]

    def _check_layout(self, total: int) -> None:
        """Entries must tile the data region in order, with no gap, overlap or tail."""
        spans = []
        for entry, name in zip(self.entries, ["<manifest>"] + self.names):
            data, used = self._read(entry)
            if len(data) != entry["length"]:
                self.bad(f"{name}: inflated to {len(data)} bytes but the entry declares {entry['length']}")
            if name != "<manifest>":
                self.files[name] = data
            physical = sum(self.blocks[k] or self.block_alloc
                           for k in range(entry["zbegin"], min(entry["zbegin"] + used, len(self.blocks))))
            spans.append((entry["offset"], entry["offset"] + physical,
                          entry["zbegin"], entry["zbegin"] + used, name))

        cursor = self.toc_length
        for lo, hi, _, _, name in spans:
            if lo != cursor:
                self.bad(f"{name}: data starts at byte {lo} but the previous entry ended at "
                         f"{cursor} ({lo - cursor:+d})")
            cursor = hi
        if cursor != total:
            self.bad(f"the last entry ends at byte {cursor} but the file is {total} bytes "
                     f"({total - cursor:+d})")

        used_counts = [0] * len(self.blocks)
        for _, _, zlo, zhi, _ in spans:
            for k in range(zlo, min(zhi, len(self.blocks))):
                used_counts[k] += 1
        orphans = [i for i, c in enumerate(used_counts) if c == 0]
        shared = [i for i, c in enumerate(used_counts) if c > 1]
        if orphans:
            self.bad(f"{len(orphans)} block size table slot(s) belong to no entry: {orphans[:10]}")
        if shared:
            self.bad(f"{len(shared)} block size table slot(s) are claimed by more than one "
                     f"entry: {shared[:10]}")

    # -- contents ----------------------------------------------------------

    def arrangements(self) -> list[dict]:
        """The instrumental/vocal attribute records the game reads when it enumerates DLC."""
        hsan = [n for n in self.names if n.endswith(".hsan")]
        if len(hsan) != 1:
            self.bad(f"expected exactly one .hsan header manifest, found {len(hsan)}")
            return []
        try:
            entries = json.loads(self.files[hsan[0]].decode("utf-8"))["Entries"]
        except Exception as e:
            self.bad(f"{hsan[0]} is not readable as the expected JSON: {e}")
            return []
        out = []
        for pid, record in entries.items():
            attrs = record.get("Attributes", {})
            name = attrs.get("ArrangementName")
            instrumental = name in INSTRUMENTAL_NAMES
            required = REQUIRED_INSTRUMENTAL_ATTRS if instrumental else REQUIRED_VOCAL_ATTRS
            missing = required - set(attrs)
            if missing:
                self.bad(f"{hsan[0]}: entry {pid} ({name}) is missing {sorted(missing)}")
            if attrs.get("PersistentID") != pid:
                self.bad(f"{hsan[0]}: entry key {pid} does not match its PersistentID "
                         f"{attrs.get('PersistentID')}")
            if instrumental:
                length = attrs.get("SongLength")
                if not isinstance(length, (int, float)) or not 0 < length < 3600:
                    self.bad(f"{hsan[0]}: {name} has SongLength {length!r}, which the game "
                             "cannot lay out")
            out.append(attrs)
        return out

    def expected_files(self) -> None:
        key = None
        for name in self.names:
            if name.endswith(".hsan"):
                key = os.path.basename(name)[len("songs_dlc_"):-len(".hsan")]
        if key is None:
            self.bad("no manifests/songs_dlc_<key>/songs_dlc_<key>.hsan entry")
            return
        wanted = [
            f"gamexblocks/nsongs/{key}.xblock",
            f"{key}_aggregategraph.nt",
            "flatmodels/rs/rsenumerable_root.flat",
            "flatmodels/rs/rsenumerable_song.flat",
            "appid.appid",
        ]
        for w in wanted:
            if w not in self.names:
                self.bad(f"missing required entry {w}")
        for size in (64, 128, 256):
            arts = [n for n in self.names if n.endswith(f"album_{key}_{size}.dds")]
            if not arts:
                self.bad(f"missing {size}px album art")
        sngs = [n for n in self.names if n.endswith(".sng")]
        generic = [n for n in sngs if "songs/bin/generic/" in n]
        macos = [n for n in sngs if "songs/bin/macos/" in n]
        if generic and macos:
            self.bad("the package holds both PC (songs/bin/generic) and Mac (songs/bin/macos) "
                     "SNG files; a package is built for one platform")
        if not sngs:
            self.bad("the package holds no SNG arrangements")
        suffix = os.path.basename(self.path)
        if generic and not suffix.endswith("_p.psarc"):
            self.bad(f"PC content but the filename does not end in _p.psarc")
        if macos and not suffix.endswith("_m.psarc"):
            self.bad(f"Mac content but the filename does not end in _m.psarc")


def report(path: str, verbose: bool) -> tuple[list[str], list[dict], str | None]:
    print(f"=== {os.path.basename(path)}  ({os.path.getsize(path):,} bytes)")
    try:
        p = Psarc(path)
    except Problem as e:
        print(f"    FATAL: {e}")
        return [str(e)], [], None
    print(f"    v{p.version[0]}.{p.version[1]}  entries={p.entry_count}  "
          f"tocLength={p.toc_length}  blockAlloc={p.block_alloc}  "
          f"{'encrypted' if p.flags == 4 else 'PLAINTEXT ToC'}")
    p.expected_files()
    arrs = p.arrangements()
    key = arrs[0].get("DLCKey") if arrs else None
    for a in arrs:
        length = a.get("SongLength")
        length = f"{length}s" if length is not None else "-"
        print(f"    {a.get('ArrangementName','?'):8} key={a.get('DLCKey')} "
              f"len={length} masterId={a.get('MasterID_RDV')} "
              f"persistentId={a.get('PersistentID')}")
    if verbose:
        for name in p.names:
            print(f"      {len(p.files.get(name, b'')):>10,}  {name}")
    for msg in p.problems:
        print(f"    !! {msg}")
    print(f"    -> {len(p.problems)} problem(s)")
    return p.problems, arrs, key


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("packages", nargs="+")
    ap.add_argument("-v", "--verbose", action="store_true", help="list every entry and its size")
    args = ap.parse_args()

    total = 0
    by_key: dict[str, list[str]] = defaultdict(list)
    by_pid: dict[str, list[str]] = defaultdict(list)
    for path in args.packages:
        problems, arrs, key = report(path, args.verbose)
        total += len(problems)
        if key:
            by_key[key].append(path)
        for a in arrs:
            by_pid[a.get("PersistentID")].append(f"{os.path.basename(path)}:{a.get('ArrangementName')}")
        print()

    if len(args.packages) > 1:
        print("=== across packages")
        # The PC and Mac build of one song legitimately share a key, but only one of the
        # two belongs in the game's dlc folder.
        for key, paths in sorted(by_key.items()):
            if len(paths) > 1:
                names = [os.path.basename(x) for x in paths]
                total += 1
                print(f"    !! DLC key {key!r} is used by {len(paths)} packages: {names}")
                print("       Install exactly one of these. Two packages sharing a key are two "
                       "songs with the same identity as far as the game is concerned.")
        for pid, where in sorted(by_pid.items()):
            if len(where) > 1:
                total += 1
                print(f"    !! PersistentID {pid} appears in {len(where)} arrangements: {where}")
        if total == 0:
            print("    no collisions")
        print()

    print(f"TOTAL PROBLEMS: {total}")
    return 1 if total else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Problem as e:
        print(f"FATAL: {e}", file=sys.stderr)
        sys.exit(2)
