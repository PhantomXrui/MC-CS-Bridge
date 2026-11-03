"""Generate independent world-glove identities and verified hand bind points.

The installed world gloves contain no root_motion bone. They must match both
hands of an already verified same-frame body and use that body's root pivot.
No first-person v_ glove or embedded default_gloves fingerprint is added.
"""
import importlib.util
import json
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
CACHE = ROOT / "build-cache/pawn-dance/agents"
spec = importlib.util.spec_from_file_location("agent_generator", ROOT / "tools/generate-agent-dance.py")
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)


def hand_pair(mesh):
    bones = list(mesh["skeleton"]["m_bones"].values())
    names = [bone["m_boneName"].lower() for bone in bones]
    result = []
    for name in ("hand_l", "hand_r"):
        assert names.count(name) == 1
        index = names.index(name)
        assert mesh["renderBones"][index]["name"].lower() == name
        inverse = np.eye(4)
        inverse[:3] = np.array(list(bones[index]["m_invBindPose"].values())).reshape(3, 4)
        point = np.linalg.solve(inverse, [0.0, 0.0, 0.0, 1.0])
        assert np.all(np.isfinite(point))
        assert np.allclose(inverse @ point, [0, 0, 0, 1], atol=1e-5)
        result.append((index + 2, point[:3]))
    return result


def compatible(first, second):
    return all(a[0] == b[0] and np.max(np.abs(a[1] - b[1])) < 0.001
               for a, b in zip(first, second))


def normalized_fingerprint(buffer):
    stride, digest, mask = generator.normalized_fingerprint(buffer)
    # Source 2 repacks legacy R8G8B8A8 normals into its runtime packed format.
    # Captured motorcycle/sporty gloves differ only in these four bytes.
    # Keep positions, bone indices/weights, and the independent IB unchanged.
    for field in buffer["layout"]:
        if field["semantic"] == "NORMAL" and field["format"] == "R8G8B8A8_UNORM":
            assert 0 <= field["offset"] <= stride - 4
            for i in range(field["offset"], field["offset"] + 4):
                mask |= 1 << i
    value = 14695981039346656037
    for i, byte in enumerate(bytes.fromhex(buffer["prefixHex"])):
        value = ((value ^ (0 if mask & (1 << (i % stride)) else byte))
                 * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return stride, f"{value:016X}", mask


def fingerprints(mesh):
    result = set()
    for buffer in mesh["buffers"]:
        assert buffer["byteLength"] >= 4096
        stride, digest, mask = normalized_fingerprint(buffer)
        result.add((buffer["byteLength"], stride, digest, mask))
    return result


def main():
    glove_metadata = json.loads((CACHE / "glove-fingerprints.json").read_text())
    bodies = json.loads((CACHE / "replay-fingerprints.json").read_text())["assets"]
    rigs = json.loads((CACHE / "replay-rig-report.json").read_text())
    assert not glove_metadata["rejected"] and not rigs["rejected"]
    world = [asset for asset in glove_metadata["assets"] if "/w_glove_" in asset["source"]]
    view = [asset for asset in glove_metadata["assets"] if "/v_" in asset["source"]]
    assert len(world) == 8 and len(view) == 14
    kind_count = rigs["kindCount"]
    kinds = {model["model"]: model["kind"] for model in rigs["models"]}
    hands, representatives = {}, {}
    for asset in bodies:
        mesh = next(mesh for mesh in asset["meshes"] if mesh["name"] == "body")
        pair = hand_pair(mesh)
        kind = kinds[asset["source"]]
        assert tuple(hand[0] for hand in pair) == (13, 14)
        if kind in hands:
            assert compatible(pair, hands[kind]), asset["source"]
        else:
            hands[kind], representatives[kind] = pair, asset["source"]
    assert set(hands) == set(range(1, kind_count + 1)) - {generator.GLOVE_KIND}
    default_meshes = 0
    for asset in bodies:
        for mesh in asset["meshes"]:
            if mesh["name"] == "default_gloves":
                assert compatible(hand_pair(mesh), hands[1]), asset["source"]
                default_meshes += 1
    assert default_meshes > 0

    entries, world_pair = set(), None
    sources = []
    for asset in world:
        assert len(asset["meshes"]) == 1
        mesh = asset["meshes"][0]
        names = [bone["m_boneName"].lower() for bone in mesh["skeleton"]["m_bones"].values()]
        assert mesh["name"] == "arms" and len(names) == 50
        assert "root_motion" not in names
        pair = hand_pair(mesh)
        assert tuple(hand[0] for hand in pair) == (4, 5)
        if world_pair is not None:
            assert compatible(pair, world_pair), asset["source"]
        else:
            world_pair = pair
        entries.update(fingerprints(mesh))
        sources.append(dict(model=asset["source"], sha256=asset["sha256"]))
    assert len(entries) == 16

    excluded = set()
    for asset in view:
        for mesh in asset["meshes"]:
            excluded.update(fingerprints(mesh))
    first_person_count = len(excluded)
    for asset in bodies:
        for mesh in asset["meshes"]:
            if mesh["name"] in ("body", "default_gloves"):
                for buffer in mesh["buffers"]:
                    if buffer["byteLength"] < 4096:
                        continue
                    stride, digest, mask = normalized_fingerprint(buffer)
                    excluded.add((buffer["byteLength"], stride, digest, mask))
    for entry in entries:
        for other in excluded:
            assert entry[1:] != other[1:] or abs(entry[0] - other[0]) >= 256, (
                "world glove identity overlaps a body or first-person buffer", entry, other)

    # Exercise the native matcher on actual prefixes, applying the candidate
    # glove's UV mask rather than trusting the other mesh's own layout mask.
    def native_match(buffer, padding):
        if buffer["byteLength"] < 4096:
            return False
        size = buffer["byteLength"] + padding
        raw = bytes.fromhex(buffer["prefixHex"])
        hashes = {}
        for expected_size, stride, digest, mask in entries:
            if stride != buffer["stride"] or not expected_size <= size < expected_size + 256:
                continue
            if mask not in hashes:
                value = 14695981039346656037
                for i, byte in enumerate(raw):
                    value = ((value ^ (0 if mask & (1 << (i % stride)) else byte))
                             * 1099511628211) & 0xFFFFFFFFFFFFFFFF
                hashes[mask] = f"{value:016X}"
            if hashes[mask] == digest:
                return True
        return False

    positive_checks = negative_checks = 0
    for asset in world:
        for mesh in asset["meshes"]:
            for buffer in mesh["buffers"]:
                for padding in (0, 1, 255):
                    assert native_match(buffer, padding), (asset["source"], buffer["kind"])
                    positive_checks += 1
    for asset in view + bodies:
        for mesh in asset["meshes"]:
            for buffer in mesh["buffers"]:
                for padding in (0, 1, 255):
                    assert not native_match(buffer, padding), (asset["source"], mesh["name"])
                    negative_checks += 1

    rewritten_attribute_checks = geometry_rejection_checks = 0
    for asset in world:
        for mesh in asset["meshes"]:
            for buffer in mesh["buffers"]:
                if buffer["stride"] <= 2:
                    continue
                stride, _, mask = normalized_fingerprint(buffer)
                raw = bytearray.fromhex(buffer["prefixHex"])
                for i in range(len(raw)):
                    if mask & (1 << (i % stride)):
                        raw[i] ^= 0xA5
                assert native_match(dict(buffer, prefixHex=raw.hex()), 0)
                rewritten_attribute_checks += 1
                for semantic in ("POSITION", "BLENDINDICES", "BLENDWEIGHT"):
                    field = next(f for f in buffer["layout"] if f["semantic"] == semantic)
                    changed = bytearray.fromhex(buffer["prefixHex"])
                    changed[field["offset"]] ^= 1
                    assert not native_match(dict(buffer, prefixHex=changed.hex()), 0)
                    geometry_rejection_checks += 1

    def fl(value):
        text = format(float(np.float32(value)), ".9g")
        return text + (".0" if "." not in text and "e" not in text else "") + "f"

    def pair_text(pair):
        return ("{" + ",".join(str(hand[0]) for hand in pair) + ",{"
                + ",".join(fl(value) for value in pair[0][1]) + "},{"
                + ",".join(fl(value) for value in pair[1][1]) + "}}")

    header = [
        "#pragma once", "#include <cstdint>", '#include "pawn_dance_agent_data.h"',
        "// Generated from installed w_ glove geometry by tools/generate-glove-dance.py.",
        "// Offsets include Source 2's two-transform prefix. These are not root bones.",
        "// Identity masks exclude material UVs and repacked legacy normals only.",
        "// GPU hand = transformPoint(transforms[base+offset], bindPoint * modelScale).",
        "namespace pawn_dance_glove_data {",
        "struct Fingerprint {unsigned bytes,stride;uint64_t prefix,uvMask;};",
        "inline constexpr Fingerprint fingerprints[]={",
    ]
    header += [f" {{{size},{stride},0x{digest}ull,0x{mask:X}ull}},"
               for size, stride, digest, mask in sorted(entries)]
    header += [
        "};",
        "struct HandPair {unsigned leftOffset,rightOffset;float leftBindPoint[3],rightBindPoint[3];};",
        "inline constexpr HandPair hands[]={",
    ]
    zero = [(0, np.zeros(3)), (0, np.zeros(3))]
    header += [" " + pair_text(hands.get(kind, zero)) + "," for kind in range(kind_count + 1)]
    header += [
        "};",
        "inline constexpr HandPair gloveHands=" + pair_text(world_pair) + ";",
        "static_assert(sizeof(hands)/sizeof(hands[0])==pawn_dance_agent_data::KindCount+1);",
        "}",
    ]
    report = dict(
        worldModels=sources, worldFingerprints=len(entries), firstPersonModels=len(view),
        firstPersonFingerprints=first_person_count, overlapWithFirstPersonOrBody=0,
        bodyKinds=kind_count - 1, bodyRepresentatives=representatives,
        validatedDefaultGloveMeshes=default_meshes,
        handOffsetsIncludeTransformPrefix=True, gloveOffsets=[4, 5], bodyOffsets=[13, 14],
        maximumAllowedBindPointDifference=0.001,
        nativeRawPrefixPositiveChecks=positive_checks,
        nativeRawPrefixNegativeChecks=negative_checks,
        rewrittenUvNormalChecks=rewritten_attribute_checks,
        changedPositionBoneWeightRejections=geometry_rejection_checks,
        validation="mesh/remapping hand names agree; finite inverse-bind origin solutions; "
                   "same-rig hand offsets and bind points agree; no +255-byte padded identity overlaps",
        limitations="metadata only; world gloves have no root_motion. Runtime must match both hands "
                    "to an unambiguous same-frame verified body and use that body's root pivot.",
    )
    generator.write_if_changed(ROOT / "source/cs2-bridge/native/pawn_dance_glove_data.h",
                               "\n".join(header) + "\n")
    generator.write_if_changed(CACHE / "glove-rig-report.json", json.dumps(report, indent=2) + "\n")
    print(json.dumps({key: value for key, value in report.items()
                      if key not in ("worldModels", "bodyRepresentatives", "validation", "limitations")}))


if __name__ == "__main__":
    main()
