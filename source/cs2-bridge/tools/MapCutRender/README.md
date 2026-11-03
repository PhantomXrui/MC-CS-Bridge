# MapCutRender

Offline C# tool that opens the extracted CS2 `de_inferno` worldnode render models
(`build-cache\inferno-vmdl\*.vmdl_c`), finds triangles whose three `POSITION`
vertices are inside a CS2 world-space box, and rewrites the triangle indices as
`(i0, i0, i0)` so the triangles render degenerate (an invisible/see-through hole
in the render mesh). One run can carry several disjoint cut zones: every model is
parsed and written at most once, and a triangle is degenerated when all three
vertices are inside any one zone. It never writes the map VPK or the game directory.

- Target framework: `net10.0`
- Parser: `ValveResourceFormat` 12.0.4261 (the version that reads these KV3 v5 resources)
- New project only; `MapPatchLab` and all other existing projects are untouched.
- `MbufWriter.cs`, `ResourceContainer.cs`, `TriangleClipper.cs` and `CutBox.cs` are
  local copies, namespace `MapPatchLab`, exactly as supplied by `MapPatchLab`.

## Build

```powershell
dotnet build `
  E:\MC-CS-Bridge\source\cs2-bridge\tools\MapCutRender\MapCutRender.csproj `
  -c Release --nologo
```

## Run

Production Inferno run (two zones: B site, already shipped, and the hull-free A-site
patch). This is also the default when no `--box`/`--min`/`--max` is given:

```powershell
dotnet `
  E:\MC-CS-Bridge\source\cs2-bridge\tools\MapCutRender\bin\Release\net10.0\MapCutRender.dll `
  --box "172,2566,-20,572,2966,180" `
  --box "1925,205,-20,2135,655,180"
```

Equivalent explicit form:

```powershell
dotnet run `
  --project E:\MC-CS-Bridge\source\cs2-bridge\tools\MapCutRender\MapCutRender.csproj `
  -c Release -- `
  --src E:\MC-CS-Bridge\build-cache\inferno-vmdl `
  --out E:\MC-CS-Bridge\build-cache\map-patch-de_inferno\variant\maps\de_inferno\worldnodes `
  --report E:\MC-CS-Bridge\build-cache\map-patch-de_inferno\render-cut-report.json `
  --min 172,2566,-20 --max 572,2966,180 `
  --min 1925,205,-20 --max 2135,655,180 --epsilon 0.01
```

Defaults: source `build-cache\inferno-vmdl`, output
`build-cache\map-patch-de_inferno\variant\maps\de_inferno\worldnodes`, report
`build-cache\map-patch-de_inferno\render-cut-report.json`, zones
`(172, 2566, -20)..(572, 2966, 180)` and `(1925, 205, -20)..(2135, 655, 180)`,
epsilon `0.01`.

Options: `--box minx,miny,minz,maxx,maxy,maxz` (repeatable), `--min`/`--max` pairs
(repeatable), `--epsilon`, `--dry-run`. Zones must be disjoint.

## Behavior

1. Every `*.vmdl_c` is parsed with VRF 12.
2. `MDAT` is read only to pair draw calls with vertex buffers. `MBUF` supplies the
   `VBIB` vertex/index buffers. Each draw call's `m_indexBuffer.m_hBuffer`,
   `m_vertexBuffers[].m_hBuffer`, `m_nStartIndex` and `m_nIndexCount` are used to
   pair ranges with vertex buffers. When no pairing can be determined, vertex
   buffer 0 is used and a warning is recorded.
3. For every 16-bit index triple, the three `POSITION:0` values must be inside one
   closed, epsilon-expanded zone box. The 2nd and 3rd indices are then set to the 1st.
4. Only resources with at least one changed triangle are written; other files are
   left untouched, and each model is written at most once per run.
5. After writing, the file is read back and parsed with VRF 12. The tool throws on
   any failure and verifies that the `MBUF` block was rewritten, that every other
   block is byte-for-byte identical, that vertex data is unchanged, that index data
   changed only in the intended degenerated slots, and that no non-degenerate
   triangle remains inside any zone.
6. The report lists every zone with its own degenerated-triangle count
   (`zones[]`), plus per-model `trianglesDegeneratedByZone`.

## Zone notes

- B site `(172, 2566, -20)..(572, 2966, 180)`: 7,135 triangles in 10 models. The
  original top `z = 145` lay below the visible render floor (nearest `POSITION`
  `(566.929, 2571.126, 159.473)`), so the top was raised to 180.
- A site `(1925, 205, -20)..(2135, 655, 180)`: 4,740 triangles in 10 models. The
  bombsite floor is at `z = 161`; this footprint is the largest patch that is free
  of convex-hull prop colliders, so the physics cutter can remove it exactly.
  A 400x400 footprint at the site is not cuttable today: `world_physics.vphys_c`
  carries thin prop/railing hulls there (up to 25 straddling hulls) and
  `PhysicsMeshCutter` deliberately never cuts convex hulls. Wider A coverage needs
  hull support, or more hull-free patches added as extra `--box` zones.

Final verified run with those two zones:

- 328 models scanned as one pass
- 20 models changed (10 B + 10 A)
- 11,875 triangles degenerated (7,135 B + 4,740 A)
- 0 errors
- the ten B-site resources come out byte-identical to the previously shipped ones
