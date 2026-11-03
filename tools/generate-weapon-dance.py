"""Append verified installed weapon meshes to the F8 attachment table.

List local VPK models with --list-models, extract that list using the existing
ReplayAgentAnalysis tool, then pass the metadata with --metadata. Existing mesh
indices and existing fingerprint rows stay unchanged. Only meshes whose first
render transform is the verified weapon_hand_r bone are accepted.
"""
import argparse
import hashlib
import json
import re
import struct
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
CACHE = ROOT / "build-cache/pawn-dance/replay-weapon-analysis"
NATIVE = ROOT / "source/cs2-bridge/native/pawn_dance_weapon_data.h"


def list_models():
    pak = ROOT / "games/Counter-Strike 2/csgo/pak01_dir.vpk"
    with pak.open("rb") as handle:
        header = handle.read(28)
        magic, version, tree_size = struct.unpack_from("<III", header)
        assert magic == 0x55AA1234 and version in (1, 2)
        handle.seek(28 if version == 2 else 12)
        tree = handle.read(tree_size)
    cursor = 0

    def string():
        nonlocal cursor
        end = tree.index(0, cursor)
        value = tree[cursor:end].decode("utf-8")
        cursor = end + 1
        return value

    models = []
    while extension := string():
        while directory := string():
            while name := string():
                _, preload, _, _, _, terminator = struct.unpack_from("<IHHIIH", tree, cursor)
                assert terminator == 0xFFFF
                cursor += 18 + preload
                if (extension == "vmdl_c" and directory.startswith("weapons/models/")
                        and name.startswith("weapon_") and not name.endswith("_mag")):
                    models.append(f"{directory}/{name}.vmdl")
    return sorted(set(models))


def floats(values):
    result = []
    for value in values:
        text = format(float(np.float32(value)), ".9g")
        result.append(text + (".0" if "." not in text and "e" not in text else "") + "f")
    return ",".join(result)


def fingerprint(raw, stride=1, mask=0):
    value = 14695981039346656037
    for index, byte in enumerate(raw):
        if mask & (1 << (index % stride)):
            byte = 0
        value = ((value ^ byte) * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return f"{value:016X}"


def uv_mask(buffer):
    if buffer["kind"] != "vb":
        return 0
    stride = buffer["stride"]
    assert 0 < stride <= 64, "vertex stride cannot fit the 64-bit UV mask"
    result = 0
    for field in buffer["layout"]:
        if field["semantic"] != "TEXCOORD" or field["index"] not in (0, 1):
            continue
        assert field["format"] in ("R16G16_SNORM", "R16G16_FLOAT", "R32G32_FLOAT"), "unverified UV format"
        size = 8 if field["format"] == "R32G32_FLOAT" else 4
        assert 0 <= field["offset"] <= stride - size
        for index in range(field["offset"], field["offset"] + size):
            result |= 1 << index
    # Never mask geometry, skinning, normals or tangents.
    for field in buffer["layout"]:
        if field["semantic"] in ("POSITION", "BLENDINDICES", "BLENDWEIGHT", "NORMAL", "TANGENT"):
            assert not result & (1 << field["offset"]), "UV overlap with non-UV input"
    return result


def weapon_points(asset, mesh):
    bones = list(mesh["skeleton"]["m_bones"].values())
    assert bones and bones[0]["m_boneName"] == "weapon_hand_r", "first mesh bone is not weapon_hand_r"
    render = mesh["renderBones"]
    assert render and render[0]["renderIndex"] == 0 and render[0]["name"] == "weapon_hand_r", "render root remap differs"
    inverse = np.eye(4)
    inverse[:3] = np.array(list(bones[0]["m_invBindPose"].values()), dtype=np.float64).reshape(3, 4)
    held = np.linalg.solve(inverse, [0, 0, 0, 1])
    assert np.all(np.isfinite(held)) and np.allclose(inverse @ held, [0, 0, 0, 1], atol=1e-6)
    holsters = asset["attachments"].get("weapon_holster_center", [])
    if not holsters:
        return held[:3], held[:3], False
    assert len(holsters) == 1, "ambiguous holster attachment"
    attachment = holsters[0]
    assert attachment["Name"] == "weapon_hand_r" and attachment["Weight"] == 1, "holster uses another bone or blended weights"
    offset = [attachment["Offset"][axis] for axis in "XYZ"] + [1]
    holster = np.linalg.solve(inverse, offset)
    assert np.all(np.isfinite(holster)) and np.allclose(inverse @ holster, offset, atol=1e-6)
    return held[:3], holster[:3], True


def generate(metadata, destination, report_path):
    data = json.loads(metadata.read_text(encoding="utf-8"))
    original = destination.read_text(encoding="utf-8")
    mesh_marker = "inline constexpr Mesh meshes[]={"
    fingerprint_marker = "inline constexpr Fingerprint fingerprints[]={"
    prefix, body = original.split(mesh_marker, 1)
    mesh_block, rest = body.split("};", 1)
    fingerprint_prefix, body = rest.split(fingerprint_marker, 1)
    fingerprint_block, suffix = body.split("};", 1)
    existing_names = re.findall(r'\{"([^"]+)"', mesh_block)
    indices = {name: index for index, name in enumerate(existing_names)}
    assert len(indices) == len(existing_names), "duplicate existing mesh names"
    old_rows = {
        (size, stride, value.upper(), index, (mask or "0").upper())
        for size, stride, value, index, mask in re.findall(
            r"\{(\d+),(\d+),0x([0-9A-Fa-f]+)ull,(\d+)(?:,0x([0-9A-Fa-f]+)ull)?\}", fingerprint_block)
    }
    fingerprint_prefix = fingerprint_prefix.replace(
        "struct Fingerprint {unsigned bytes,stride;uint64_t prefix;unsigned mesh;};",
        "struct Fingerprint {unsigned bytes,stride;uint64_t prefix;unsigned mesh;uint64_t uvMask=0;};")
    mesh_lines, buffer_lines, accepted, rejected, excluded = [], [], [], list(data.get("rejected", [])), []
    largest = 0
    for asset in sorted(data["assets"], key=lambda entry: entry["source"]):
        for mesh in asset["meshes"]:
            name = asset["source"] + "#" + mesh["name"]
            try:
                held, holster, has_holster = weapon_points(asset, mesh)
                eligible = []
                for buffer in mesh["buffers"]:
                    size, stride = buffer["byteLength"], buffer["stride"]
                    if size < 4096:
                        excluded.append(dict(mesh=name, kind=buffer["kind"], index=buffer["index"],
                                             reason="buffer below native 4096-byte identification minimum"))
                        continue
                    raw = bytes.fromhex(buffer["prefixHex"])
                    assert len(raw) == 4096
                    assert 0 < stride <= 128
                    largest = max(largest, size)
                    eligible.append((size, stride, fingerprint(raw), 0, buffer))
                    mask = uv_mask(buffer)
                    if mask:
                        eligible.append((size, stride, fingerprint(raw, stride, mask), mask, buffer))
                assert eligible, "no buffers large enough for native identity"
                added = name not in indices
                if added:
                    indices[name] = len(indices)
                    mesh_lines.append(' {"' + name + '",{' + floats(held) + "},{" + floats(holster)
                                      + "}," + str(has_holster).lower() + "},")
                index = indices[name]
                for size, stride, value, mask, buffer in eligible:
                    row = str(size), str(stride), value, str(index), f"{mask:X}"
                    if row in old_rows:
                        continue
                    old_rows.add(row)
                    buffer_lines.append(f" {{{size},{stride},0x{value}ull,{index},0x{mask:X}ull}},"
                                        f" // {Path(asset['source']).stem}/{mesh['name']}/{buffer['kind']}{buffer['index']}")
                accepted.append(dict(mesh=name, index=index, added=added, held=held.tolist(),
                                     holster=holster.tolist(), hasHolster=has_holster,
                                     modelSha256=asset["sha256"], buffers=len(eligible)))
            except (AssertionError, KeyError, ValueError, np.linalg.LinAlgError) as error:
                rejected.append(dict(mesh=name, reason=str(error)))
    assert len(indices) <= 256, f"{len(indices)} meshes exceed coordinated 256-bit native identity mask"
    generated = (prefix + mesh_marker + mesh_block + "\n".join(mesh_lines) + ("\n" if mesh_lines else "")
                 + "};" + fingerprint_prefix + fingerprint_marker + fingerprint_block
                 + "\n".join(buffer_lines) + ("\n" if buffer_lines else "") + "};" + suffix)
    assert re.findall(r'\{"([^"]+)"', generated.split(mesh_marker, 1)[1].split("};", 1)[0])[:len(existing_names)] == existing_names
    # Preserve a reproducible baseline of the exact installed index layout.
    CACHE.mkdir(parents=True, exist_ok=True)
    baseline = CACHE / "weapon-data-before-expansion.h"
    if not baseline.exists():
        baseline.write_text(original, encoding="utf-8", newline="\n")
    destination.write_text(generated, encoding="utf-8", newline="\n")
    report = dict(sourceMetadata=str(metadata), sourceMetadataSha256=hashlib.sha256(metadata.read_bytes()).hexdigest(),
                  priorMeshCount=len(existing_names), meshCount=len(indices), appendedMeshes=len(mesh_lines),
                  appendedFingerprints=len(buffer_lines), totalFingerprints=len(old_rows),
                  largestBufferBytes=largest, requiredMaskBits=len(indices), accepted=accepted,
                  rejected=rejected, excludedBuffers=excluded,
                  validation="weapon_hand_r at mesh/render index0; finite inverse-bind anchors; "
                             "single weight1 holster; original index prefix preserved; local 4096-byte FNV identities; "
                             "only metadata-declared TEXCOORD0/1 bytes masked for painted materials",
                  limitation="asset metadata validation only; exact live weapon ownership remains checked by GPU socket matching")
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({key: report[key] for key in ("priorMeshCount", "meshCount", "appendedMeshes",
                                                 "appendedFingerprints", "totalFingerprints", "largestBufferBytes")}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--list-models", type=Path)
    parser.add_argument("--metadata", type=Path)
    parser.add_argument("--output", type=Path, default=NATIVE)
    parser.add_argument("--report", type=Path, default=CACHE / "weapon-rig-report.json")
    args = parser.parse_args()
    if args.list_models:
        models = list_models()
        args.list_models.parent.mkdir(parents=True, exist_ok=True)
        args.list_models.write_text(json.dumps(models, indent=2), encoding="utf-8")
        print(f"Listed {len(models)} installed weapon models (detached magazines excluded)")
    if args.metadata:
        generate(args.metadata, args.output, args.report)
    if not args.metadata and not args.list_models:
        parser.error("Provide --list-models or --metadata")


if __name__ == "__main__":
    main()
