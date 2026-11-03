# Local squash-dance props

This converter reads the user's existing offline CS2 `csgo/pak01_dir.vpk` and writes derived models only to `D:\MC-CS-Bridge\build-cache\dance-models`. It never edits or replaces original player models. Generated game assets are local-only and must not be redistributed or committed.

Run with the existing .NET 8 runtime:

```powershell
dotnet run --project tools/SquashDanceModels -- D:\MC-CS-Bridge characters/models/tm_leet/tm_leet_varianta.vmdl_c
```

The root and source model are optional and default to the above. Each successful run merges its model record into `manifest.json`, so more locally owned T models can be added independently. Uninspected vertex layouts fail rather than produce a guessed conversion.

The manifest has `version: 1`, `steps: 17`, and `models: [{source, frames}]`. Frame names omit the compiled `_c` suffix for engine `SetModel` calls. Copy the generated `models` directory to the existing offline game's `csgo/models`, preserving its hierarchy. Copy `manifest.json` to the bridge runtime location selected by the main plugin. Precache these model paths before spawning visual props.

For elapsed milliseconds `t` and default 126 BPM:

```
squash = (1 - cos(2 * pi * t * 126 / 60000)) / 2
frame = round(squash * 16)
height = 1 - 0.82 * frame / 16
width = depth = 1 + 0.55 * frame / 16
```

The converter genuinely changes every position vertex in X/Y/Z. The bottom of the mesh is anchored at local Z=0. Each prop is the original recognizable model/material in a fixed bind pose, with one identity root bone and every vertex weighted to that root. Model/mesh bounds are rewritten. Original animation graph, embedded animation/physics declarations, mesh hitboxes, attachments, and constraints are removed from the generated prop's active declarations. Original material references still resolve from the owned game.

The main plugin must use these only as non-colliding visual props following the live pawn. Hide the original pawn while dancing and restore its exact prior rendering state when stopped, on disconnect, death, round end, and unload. Do not change real pawn collision, health, scale, or movement. A fallback model may change another T agent's visible appearance during the dance and must be described honestly.

Offline checks reparse all 17 resources, compare every generated vertex against the axis deformation formula, verify the single-bone skin weights, and check that no active embedded animation remains. Per-model JSON records include AABBs and SHA256 hashes. These checks do not establish engine acceptance; real in-game rendering remains required.

Lighting limitation: the initial converter preserves the original compressed normal/tangent frames, so shading under extreme nonuniform compression is approximate. Actual geometry and silhouettes are deformed correctly.

Format writers are linked from the existing `MapPatchLab` implementation without modifying it. The KV3 writer preserves its ValveResourceFormat attribution/license notice.
