"""Extract one map's local physics chunk (vphys_c) from its own VPK.

Usage: python extract-map-physics-any.py <map name> [root]

Reads  <root>/games/Counter-Strike 2/csgo/maps/<map>.vpk
Writes <root>/build-cache/map-physics/<map>/<entry>.vphys_c
Never writes into the game tree; generated data stays local.
"""
import os
import pathlib
import struct
import sys

name = sys.argv[1] if len(sys.argv) > 1 else "de_nuke"
root = pathlib.Path(sys.argv[2] if len(sys.argv) > 2 else os.environ.get("MCCS_ROOT", pathlib.Path(__file__).resolve().parents[1]))
vpk = root / "games/Counter-Strike 2/csgo/maps" / (name + ".vpk")
if not vpk.is_file():
    raise SystemExit("missing map vpk: " + str(vpk))

b = vpk.read_bytes()
magic, version, size = struct.unpack_from("<III", b)
assert magic == 0x55AA1234, "not a VPK"
header = 28 if version == 2 else 12
cursor = header
out = root / "build-cache/map-physics" / name
out.mkdir(parents=True, exist_ok=True)


def string():
    global cursor
    end = b.index(0, cursor)
    value = b[cursor:end].decode("utf-8")
    cursor = end + 1
    return value


written = []
while True:
    ext = string()
    if not ext:
        break
    while True:
        directory = string()
        if not directory:
            break
        while True:
            entry = string()
            if not entry:
                break
            crc, preload, archive, offset, length, term = struct.unpack_from("<IHHIIH", b, cursor)
            cursor += 18
            prefix = b[cursor:cursor + preload]
            cursor += preload
            if ext != "vphys_c":
                continue
            assert archive == 0x7FFF, "external archive not supported"
            dest = out / (entry + "." + ext)
            dest.write_bytes(prefix + b[header + size + offset:header + size + offset + length])
            written.append((dest.name, length))

for name_, length in written:
    print("extracted", name_, length)
if not written:
    raise SystemExit("no vphys_c entry found")
