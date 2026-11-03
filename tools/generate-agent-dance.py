"""Build verified F8 rig tables from metadata extracted from the installed VPK.

The default mode retains the established bridge model list. --replay adds every
validated body in replay-fingerprints.json without changing the C# catalog.
Kinds 1 (Phoenix), 2 (Leet), and 3 (shared gloves) are protocol constants.
"""
import argparse
import hashlib
import json
import re
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
PHOENIX = "characters/models/tm_phoenix/tm_phoenix.vmdl"
LEET = "characters/models/tm_leet/tm_leet_varianta.vmdl"
SAS = "characters/models/ctm_sas/ctm_sas.vmdl"
GLOVE_KIND = 3
ANCHOR_NAMES = ["weapon_hand_r", "knife", "pistol", "primary"] + [
    f"grenade{i}" for i in range(5)
] + ["c4"]


def normalized_fingerprint(buffer):
    """Ignore only the UV bytes Source 2 rewrites for material variants."""
    mask = 0
    stride = buffer["stride"]
    assert 0 < stride <= 64
    for field in buffer["layout"]:
        if field["semantic"] != "TEXCOORD" or field["index"] not in (0, 1):
            continue
        assert field["format"] in ("R16G16_SNORM", "R16G16_FLOAT", "R32G32_FLOAT")
        size = 8 if field["format"] == "R32G32_FLOAT" else 4
        assert 0 <= field["offset"] <= stride - size
        for i in range(field["offset"], field["offset"] + size):
            mask |= 1 << i
    raw = bytes.fromhex(buffer["prefixHex"])
    assert len(raw) == min(4096, buffer["byteLength"])
    value = 14695981039346656037
    for i, byte in enumerate(raw):
        if mask & (1 << (i % stride)):
            byte = 0
        value = ((value ^ byte) * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    buffer["identityHash"] = f"{value:016X}"
    buffer["identityMask"] = mask
    return stride, buffer["identityHash"], mask


def read_profile(asset):
    bodies = [mesh for mesh in asset["meshes"] if mesh["name"] == "body"]
    assert len(bodies) == 1, "expected one body mesh"
    mesh = bodies[0]
    bones = list(mesh["skeleton"]["m_bones"].values())
    names = [bone["m_boneName"] for bone in bones]
    assert names.count("root_motion") == 1, "missing or duplicate body root_motion"
    root = names.index("root_motion")
    # Mesh-local skeleton and model remapping must identify the same root.
    assert mesh["renderBones"][root]["name"] == "root_motion", "root remapping differs"
    anchors = []
    for name in ANCHOR_NAMES:
        attachments = asset["attachments"][name]
        assert len(attachments) == 1 and attachments[0]["Weight"] == 1, name
        attachment = attachments[0]
        assert names.count(attachment["Name"]) == 1, f"{name} bone not unique"
        index = names.index(attachment["Name"])
        assert mesh["renderBones"][index]["name"] == attachment["Name"], name
        inverse = np.eye(4)
        inverse[:3] = np.array(list(bones[index]["m_invBindPose"].values())).reshape(3, 4)
        offset = np.array([*(attachment["Offset"][axis] for axis in "XYZ"), 1.0])
        point = np.linalg.solve(inverse, offset)
        assert np.all(np.isfinite(point)), f"{name} has nonfinite bind point"
        assert np.allclose(inverse @ point, offset, atol=1e-5), name
        anchors.append((index + 2, int(name != "weapon_hand_r"), point[:3].tolist()))
    for glove in (mesh for mesh in asset["meshes"] if mesh["name"] == "default_gloves"):
        glove_names = [bone["m_boneName"] for bone in glove["skeleton"]["m_bones"].values()]
        assert glove_names.count("root_motion") == 1 and glove_names.index("root_motion") == 38
        assert glove["renderBones"][38]["name"] == "root_motion", "glove root remapping differs"
    key = json.dumps([root, [(off, kind, np.round(point, 3).tolist())
                            for off, kind, point in anchors]])
    return dict(root=root, anchors=anchors, key=key)


def build(assets, replay, extraction_rejected):
    profiles, model_profiles, accepted = [], [], []
    rejected = list(extraction_rejected)
    skipped_buffers, excluded_meshes = [], []
    for asset in assets:
        try:
            profile = read_profile(asset)
            for mesh in asset["meshes"]:
                if mesh["name"] not in ("body", "default_gloves"):
                    excluded_meshes.append(dict(model=asset["source"], mesh=mesh["name"],
                                                reason="not a validated body/glove mesh"))
                    continue
                for buffer in mesh["buffers"]:
                    normalized_fingerprint(buffer)
                    if buffer["byteLength"] < 4096:
                        skipped_buffers.append(dict(model=asset["source"], mesh=mesh["name"],
                                                    kind=buffer["kind"], index=buffer["index"],
                                                    bytes=buffer["byteLength"],
                                                    reason="native identifier requires 4096 bytes"))
            index = next((i for i, existing in enumerate(profiles)
                          if existing["key"] == profile["key"]), len(profiles))
            if index == len(profiles):
                profiles.append(profile)
            model_profiles.append(index)
            accepted.append(asset)
        except (AssertionError, KeyError, ValueError, np.linalg.LinAlgError) as error:
            if not replay or asset["source"] in (PHOENIX, LEET, SAS):
                raise ValueError(f"{asset['source']}: {error}") from error
            rejected.append(dict(source=asset["source"], message=str(error)))

    parent = list(range(len(profiles)))

    def find(index):
        while parent[index] != index:
            index = parent[index]
        return index

    # Shared body buffers can merge only profiles with the same verified root.
    # Union verified sockets; never infer a root or guess nearest ownership.
    seen = {}
    for asset, index in zip(accepted, model_profiles):
        body = next(mesh for mesh in asset["meshes"] if mesh["name"] == "body")
        for buffer in body["buffers"]:
            if buffer["byteLength"] < 4096:
                continue
            key = buffer["stride"], buffer["identityHash"], buffer["identityMask"]
            if key in seen:
                other = seen[key]
                assert profiles[index]["root"] == profiles[other]["root"], (
                    "shared fingerprint has incompatible root", asset["source"], key)
                low, high = sorted((find(index), find(other)))
                parent[high] = low
            else:
                seen[key] = index

    profile_by_model = {asset["source"]: index for asset, index in zip(accepted, model_profiles)}
    phoenix_group, leet_group = find(profile_by_model[PHOENIX]), find(profile_by_model[LEET])
    assert phoenix_group != leet_group
    groups = list(dict.fromkeys(find(index) for index in model_profiles))
    kinds = {phoenix_group: 1, leet_group: 2}
    next_kind = 4
    for group in groups:
        if group not in kinds:
            kinds[group] = next_kind
            next_kind += 1
    model_kinds = [kinds[find(index)] for index in model_profiles]
    kind_count = max([GLOVE_KIND, *model_kinds])
    roots = [38] * (kind_count + 1)
    all_anchors, ranges = [], [(0, 0)] * (kind_count + 1)
    for group, kind in sorted(kinds.items(), key=lambda item: item[1]):
        roots[kind] = profiles[group]["root"]
        start, keys = len(all_anchors), set()
        for index, profile in enumerate(profiles):
            if find(index) != group:
                continue
            for offset, point_kind, point in profile["anchors"]:
                key = offset, point_kind, tuple(np.round(point, 3))
                if key not in keys:
                    all_anchors.append((kind, offset, point_kind, point))
                    keys.add(key)
        ranges[kind] = (start, len(all_anchors))
    ranges[GLOVE_KIND] = (ranges[2][1], ranges[2][1])
    assert roots[1:4] == [38, 39, 38]

    fingerprints = {}
    for asset, kind in zip(accepted, model_kinds):
        for mesh in asset["meshes"]:
            if mesh["name"] not in ("body", "default_gloves"):
                continue
            mesh_kind = kind if mesh["name"] == "body" else GLOVE_KIND
            for buffer in mesh["buffers"]:
                if buffer["byteLength"] < 4096:
                    continue
                key = (buffer["byteLength"], buffer["stride"],
                       buffer["identityHash"], buffer["identityMask"])
                # Native matching permits +255 byte padding. Overlapping ranges
                # with the same prefix must not identify different skeletons.
                for old_key, old_kind in fingerprints.items():
                    if old_key[1:] == key[1:] and abs(old_key[0] - key[0]) < 256:
                        assert old_kind == mesh_kind, ("ambiguous rig identity", asset["source"], key)
                fingerprints[key] = mesh_kind

    def fl(value):
        text = format(float(np.float32(value)), ".9g")
        return text + (".0" if "." not in text and "e" not in text else "") + "f"

    header = [
        "#pragma once", "#include <cstdint>",
        "// Generated from the installed VPK by tools/generate-agent-dance.py"
        + (" --replay." if replay else "."),
        "namespace pawn_dance_agent_data {",
        f"inline constexpr int KindCount={kind_count};",
        f"inline constexpr int GloveKind={GLOVE_KIND};",
        "inline constexpr unsigned roots[]={" + ",".join(map(str, roots)) + "};",
        "struct Fingerprint {unsigned bytes,stride;uint64_t prefix;int kind;uint64_t uvMask;};",
        "inline constexpr Fingerprint fingerprints[]={",
    ]
    header += [f" {{{size},{stride},0x{value}ull,{kind},0x{mask:X}ull}},"
               for (size, stride, value, mask), kind in fingerprints.items()]
    header += ["};", "struct Anchor {unsigned kind,transformOffset,weaponPoint;float point[3];};",
               "inline constexpr Anchor anchors[]={"]
    header += [f" {{{kind},{offset},{point_kind},{{" + ",".join(fl(value) for value in point) + "}},"
               for kind, offset, point_kind, point in all_anchors]
    header += ["};", "inline constexpr unsigned anchorRanges[][2]={"
               + ",".join("{" + f"{start},{end}" + "}" for start, end in ranges) + "};", "}"]
    report = dict(
        mode="replay" if replay else "bridge",
        models=[dict(model=asset["source"], kind=kind, root=roots[kind], sourceSha256=asset["sha256"])
                for asset, kind in zip(accepted, model_kinds)],
        rigs=len(groups), kindCount=kind_count, fingerprints=len(fingerprints),
        anchors=len(all_anchors), gloveKind=GLOVE_KIND, rejected=rejected,
        skippedBuffers=skipped_buffers, excludedMeshes=excluded_meshes,
        validation="mesh skeleton/remapping root agreement; all ten sockets including C4 from inverse bind poses; "
                   "finite points; compatible-root fingerprint unions; no padded-range rig ambiguity",
        limitations="asset metadata verified; live replay rendering is not certified by this report; "
                    "small draw buffers and defusekit meshes are excluded",
    )
    return "\n".join(header) + "\n", report


def write_if_changed(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists() or path.read_text(encoding="utf-8") != text:
        path.write_text(text, encoding="utf-8", newline="\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--replay", action="store_true")
    parser.add_argument("--metadata", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    src = ROOT / "source/cs2-bridge"
    cache = ROOT / "build-cache/pawn-dance/agents"
    metadata = args.metadata or cache / ("replay-fingerprints.json" if args.replay else "fingerprints.json")
    data = json.loads(metadata.read_text(encoding="utf-8"))
    pool = (src / "plugin/UniqueTAgentCatalog.cs").read_text(encoding="utf-8").split("readonly record struct")[0]
    selected = set(re.findall(r'"(characters/[^"]+\.vmdl)"', pool))
    assert len(selected) == 7
    bridge_data = json.loads((cache / "fingerprints.json").read_text(encoding="utf-8"))
    bridge_paths = [asset["source"] for asset in bridge_data["assets"]
                    if asset["source"] in selected | {PHOENIX, LEET}]
    assert len(bridge_paths) == 9 and bridge_paths[:2] == [PHOENIX, LEET]
    by_path = {asset["source"]: asset for asset in data["assets"]}
    paths = bridge_paths
    if args.replay:
        assert SAS in by_path, "replay data must include the installed default SAS model"
        paths = bridge_paths + [SAS] + sorted(set(by_path) - set(bridge_paths) - {SAS})
    assets = [by_path[path] for path in paths]
    header, report = build(assets, args.replay, data.get("rejected", []))
    report["metadataSha256"] = hashlib.sha256(metadata.read_bytes()).hexdigest()
    output = args.output or src / "native/pawn_dance_agent_data.h"
    report_path = args.report or cache / ("replay-rig-report.json" if args.replay else "rig-report.json")
    write_if_changed(output, header)
    write_if_changed(report_path, json.dumps(report, indent=2) + "\n")
    if not args.replay:
        catalog = ["// Generated from local model geometry and socket metadata.",
                   "namespace MinecraftBridge;", "public sealed partial class MinecraftBridgePlugin", "{",
                   "    static readonly Dictionary<string,int> DanceModelKinds = new(StringComparer.OrdinalIgnoreCase)",
                   "    {"]
        catalog += [f'        ["{model["model"]}"] = {model["kind"]},' for model in report["models"]]
        catalog += ["    };", "}"]
        write_if_changed(src / "plugin/DanceModelCatalog.cs", "\n".join(catalog) + "\n")
    print(json.dumps({key: report[key] for key in ("mode", "rigs", "kindCount", "fingerprints", "anchors", "gloveKind")}))
    print(json.dumps(dict(models=len(report["models"]), rejected=len(report["rejected"]),
                          skippedBuffers=len(report["skippedBuffers"]), excludedMeshes=len(report["excludedMeshes"]))))


if __name__ == "__main__":
    main()
