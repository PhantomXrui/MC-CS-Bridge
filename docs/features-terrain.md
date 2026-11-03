# 地形破坏、方块碰撞与 C4 联动：实现和复现

这里解释的是仓库中已经写出的程序及其限制。核心做法是：离线修改本机地图的指定区域，把原生地板预先挖开；运行时用真实 MC 方块填住洞口；方块消失时撤掉对应的 CS 碰撞体，露出已经存在的洞。它没有在 C4 爆炸瞬间重新切割整张 CS 地图。

公开仓库提供算法、插件、工具和配置；原始地图、修改后的 VPK、`world.tri`、MC 存档都需要使用者在本机准备。本文的命令不是完整游戏安装器，依赖版本和基础桥接安装见 [build.md](build.md)。

## 1. 从触发到结果的完整链路

| 阶段 | 输入与处理 | 输出及代码入口 |
|---|---|---|
| 离线预切 | 本地地图 VPK + CS 世界坐标下的切割盒；检查凸包，切掉三角网格表面 | 新物理资源、渲染资源、派生 VPK、桥接几何缓存；[MapCutRunner.Run](../source/cs2-bridge/tools/MapPatchLab/MapCutRunner.cs) |
| 坐标映射 | 以 40 CS 单位对应 1 MC 方块，交换竖直轴并反转水平轴 | [WorldGeometry.ToMC / ToCS](../source/cs2-bridge/plugin/WorldGeometry.cs) |
| 覆盖洞口 | MC `fill` 放置泥土；MC 获取实际方块碰撞形状 | `blocks` 消息里的 `shapes`、`materials`、`set`、`clear`；[WorldBridge.flush / emitShapes](../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/WorldBridge.java) |
| CS 跟随碰撞 | 接收每个方块的形状，在 CS 中建立透明、冻结的物理实体 | [ApplyBlockUpdates / UpdateBlockShape](../source/cs2-bridge/plugin/BlockColliders.cs) |
| 爆炸触发 | `TrackPlantedC4` 记住炸弹位置，`EventBombExploded` 调用 `SpawnMinecraftTnt` | 对应 MC 区域清空/召唤 TNT；[ObsidianBlastShield.cs](../source/cs2-bridge/plugin/ObsidianBlastShield.cs) |
| 洞口开放 | MC 方块变成空气，发送空形状/清除消息；CS 删除方块碰撞实体 | `RemoveBlockCollider`；角色开始受到原生洞口几何约束 |

普通挖方块或 MC TNT 破坏走同一条“MC 状态变化 → `blocks` → CS 碰撞更新”链路，不需要伪造另一个 CS 地面位置。

## 2. 地图工具究竟做了什么

### 2.1 提取原始物理缓存

[extract-map-physics-any.py](../tools/extract-map-physics-any.py) 读取 VPK 目录树，只提取内嵌 archive `0x7FFF` 的 `vphys_c` 条目。它支持地图名和根目录参数，不支持分卷外部 archive。它只提取物理资源，不会提取 `worldnodes/*.vmdl_c` 渲染模型。

[MapExport/Program.cs](../source/cs2-bridge/tools/MapExport/Program.cs) 随后解析物理资源，把 `Default` 碰撞组的 mesh 三角形，以及 hull 面扇形化后的三角形写入 `world.tri`。格式是魔数 `0x3152544d`、三角形数量，然后每个三角形三个顶点的 float32 XYZ。这个缓存供桥接查询，不等于游戏地图本身。

以下 PowerShell 命令从公开仓库根目录运行。先将游戏资源按自己的合法安装整理到工作区规定位置；如果缺少源文件，停在这里处理依赖，不要下载别人的缓存来绕过版本匹配。

```powershell
$env:MCCS_ROOT = (Get-Location).Path
$TerrainMap = 'de_inferno'
$TerrainVpk = Join-Path $env:MCCS_ROOT "games/Counter-Strike 2/csgo/maps/$TerrainMap.vpk"
if (-not (Test-Path -LiteralPath $TerrainVpk)) { throw "缺少本地地图：$TerrainVpk" }
python tools/extract-map-physics-any.py $TerrainMap $env:MCCS_ROOT
if ($LASTEXITCODE -ne 0) { throw '物理提取失败' }
dotnet run --project source/cs2-bridge/tools/MapExport/MapExport.csproj -c Release -- `
  "$env:MCCS_ROOT/build-cache/map-physics/$TerrainMap/world_physics.vphys_c" `
  "$env:MCCS_ROOT/build-cache/map-physics/$TerrainMap/world.tri"
if ($LASTEXITCODE -ne 0) { throw '物理缓存导出失败' }
```

成功判据：提取器打印 `extracted world_physics.vphys_c ...`，MapExport 打印非零三角形数量，目标 `world.tri` 存在。这只能证明提取/导出完成，尚未修改地形，也未证明引擎加载成功。

[vpk-list.py](../tools/vpk-list.py) 是辅助查找脚本：实际只打印路径含 `cube`、`box` 或 `crate_64` 的 `vmdl_c` 名称，用来查可用碰撞立方体模型；它不是通用 VPK 解包器。

### 2.2 物理预切和精确渲染切割

[PhysicsMeshCutter.CutMeshOnly](../source/cs2-bridge/tools/MapPatchLab/PhysicsMeshCutter.cs) 对 mesh 三角形逐个做盒子减法。[TriangleClipper.SubtractBox](../source/cs2-bridge/tools/MapPatchLab/TriangleClipper.cs) 依次按盒子六个平面分割多边形，保留盒外部分并重新三角化。随后重建 BVH，检查节点范围、子节点、覆盖关系，保留每个三角形的材料索引。

只要任意原生凸包的包围盒与切割区重叠，工具就拒绝完成该次切割；它没有实现凸包布尔减法。诊断输出中的 `fully inside (removable)` 是旧提示，不能理解成当前切割器会删除这些凸包：`HullsInside` 和 `HullsOverlappingNotInside` 都应为零。

[Kv3V4Writer.Write / AssertEquivalent](../source/cs2-bridge/tools/MapPatchLab/Kv3V4Writer.cs) 将修改后的 KV3 树写回，并重新解析验证逻辑树等价。它要求匹配的资源格式，不是对所有版本 Source 2 资源的通用编辑器。

默认的 [ExactRenderCutter.Cut](../source/cs2-bridge/tools/MapPatchLab/ExactRenderCutter.cs) 会产生新的边界顶点，插值位置、UV、光照图坐标与颜色，重建 meshlet 和资源计数；压缩法线/切线采用近源顶点近似。这条路线会同时改变顶点/索引及相关元数据，需要逐图确认资源格式。

当前 CLI 的 `MapCutRunner.Run` 只枚举 `maps/<map>/world_physics.vphys_c`，并按选项处理相交的 worldnode 模型。仓库还保留 [NativeCutExperiment.Run](../source/cs2-bridge/tools/MapPatchLab/NativeCutExperiment.cs)，它是早期 Dust2 固定区域实验，会处理 `world_physics.vphys_c` 和 `world_physics.vmdl_c` 的内嵌 PHYS；当前 CLI 不会自动调用这个旧入口。不要把旧实验报告中的双资源处理当成现在每张地图的默认行为。

### 2.3 Inferno 的四个区域

当前 [MapCutRender 的 DefaultBoxes](../source/cs2-bridge/tools/MapCutRender/Program.cs) 使用下面的 CS 坐标。它们对应特定本地地图资源，换版本仍必须重新诊断。

| 区域 | 最小 XYZ | 最大 XYZ |
|---|---|---|
| B | `172,2566,-20` | `572,2966,180` |
| A1 | `1925,205,-20` | `2135,655,180` |
| A2 | `2179,205,-20` | `2379,430,180` |
| A3 | `2179,430,-20` | `2379,655,180` |

A 区 X=`2135..2179` 的间隔保留，因为此处有当前流程不能切割的凸包。MC 的 A 区填充范围是一个较大的整块包围区，所以“MC 填充/清空范围”与“真正的原生洞口范围”并不完全相同。

先做只读诊断，输入仍是原始本地 VPK：

```powershell
$TerrainCutArgs = @(
  '--root', $env:MCCS_ROOT, '--map', 'de_inferno',
  '--box', '172,2566,-20,572,2966,180',
  '--box', '1925,205,-20,2135,655,180',
  '--box', '2179,205,-20,2379,430,180',
  '--box', '2179,430,-20,2379,655,180'
)
dotnet run --project source/cs2-bridge/tools/MapPatchLab/MapPatchLab.csproj -c Release -- @TerrainCutArgs --diagnose
if ($LASTEXITCODE -ne 0) { throw '区域诊断失败' }
Get-Content "$env:MCCS_ROOT/build-cache/map-patch-de_inferno/cut-diagnostics.json"
```

成功判据：四个区都存在可切的 mesh 表面；两类 hull 计数均为零；坐标高度与目标地面一致。若只有坐标范围正确、`MeshTrianglesPositiveAreaInside` 为零，不能继续宣称切割有作用。

之后在缓存目录生成物理修改。`--physics-only` 表示不走 ExactRenderCutter；`--inplace` 仅保留资源命名空间为 `de_inferno`，**不表示覆盖安装目录的 VPK**：

```powershell
dotnet run --project source/cs2-bridge/tools/MapPatchLab/MapPatchLab.csproj -c Release -- @TerrainCutArgs --physics-only --inplace
if ($LASTEXITCODE -ne 0) { throw '物理预切失败' }
```

输入是工作区游戏目录下的原图，输出位于 `build-cache/map-patch-de_inferno/`：

- `variant/maps/de_inferno/world_physics.vphys_c`：修改后的物理资源。
- `native-cut-report.json`：切割、面积变化、回读检查结果。
- `de_inferno.vpk` 和 `map-package-report.json`：本阶段派生包与封包检查结果。
- `build-cache/map-physics/de_inferno/world.tri`：从修改后物理导出的桥接缓存，已检查切割盒中不残留正面积表面。

此时还没有完成 Inferno 可见地面处理，不能立即把生成的第一阶段 VPK 当成最终效果。

### 2.4 Inferno 渲染处理和最终封包

[MapCutRender.ProcessModel / VerifyPatch](../source/cs2-bridge/tools/MapCutRender/Program.cs) 采用较保守的另一条路线：只有三角形三个 POSITION 顶点全部落在同一个切割盒内，才把该三角形索引退化。它只重写 MBUF，并验证其他资源块保持不变；跨越切割边界的大三角形不会被精确裁开。因此它与物理切割可能存在边缘差异。

这里需要先用自己本地可用的资源工具，从原图提取 `maps/de_inferno/worldnodes/*.vmdl_c` 到 `build-cache/inferno-vmdl`。公开仓库不包含这些文件；上述 Python 物理提取器不能代替这个步骤。使用过的两个 C# 地图工具依赖不同 VRF 版本，按各自项目还原依赖。

公开版 MapCutRender 现在从绝对路径环境变量 `MCCS_ROOT` 派生默认输入和输出；即使显式传 `--out` / `--report`，输出也必须留在该工作区 `build-cache` 下。先试算，再正式写入：

```powershell
dotnet run --project source/cs2-bridge/tools/MapCutRender/MapCutRender.csproj -c Release -- --dry-run
if ($LASTEXITCODE -ne 0) { throw '渲染试算失败' }
Get-Content "$env:MCCS_ROOT/build-cache/map-patch-de_inferno/render-cut-report.json"
# 核对报告后执行：
dotnet run --project source/cs2-bridge/tools/MapCutRender/MapCutRender.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw '渲染修改失败' }
# 在原图尚未替换的前提下，重新封包，让 variant 中的渲染修改进入最终 VPK：
dotnet run --project source/cs2-bridge/tools/MapPatchLab/MapPatchLab.csproj -c Release -- @TerrainCutArgs --physics-only --inplace
if ($LASTEXITCODE -ne 0) { throw '最终封包失败' }
```

`--dry-run` 不写修改后的模型，但仍写 JSON 报告。成功判据不是仅看退出码：`totalErrors=0`、`modelsScanned>0`、预期区域的 `trianglesDegenerated>0`，且正式报告的 `dryRun=false`。空目录也可能以退出码 0 结束，必须检查这些数量。

[MapNamespaceBuilder.Build](../source/cs2-bridge/tools/MapPatchLab/MapNamespaceBuilder.cs) 读取原图，并优先使用 `variant` 中的同名替换资源，写出派生包后重开、验证校验和并逐条比较资源字节。不带 `--inplace` 时会生成 `_mccs` 命名空间并重写支持的结构化引用；更高版本 KV3 等情况仍有兼容限制。所有这些验证都是文件级验证，程序不会自动安装或启动游戏。

`variant` 会保留之前的文件。改变切割盒、工具路线或地图版本时，应为旧缓存改名归档后重建，避免旧模型修改混入新包。安装前保留原始资源，并在独立离线测试副本中使用生成的包；不得让下一次构建把已经修改的地图误当原始输入。

## 3. 坐标、地面覆盖和建筑碰撞

默认对应关系为：

```text
MC.x = CS.x / 40
MC.y = CS.z / 40 + 96
MC.z = -CS.y / 40
CS.x = MC.x * 40
CS.y = -MC.z * 40
CS.z = (MC.y - 96) * 40
```

`WorldGeometry.ToMC/ToCS` 的 96 是固定值；其他运行模块还有高度偏移配置，修改配置前应核对所有路径一致。只改一处会出现地面、角色或射线整体错层。

[WorldGeometry.SurfaceCells](../source/cs2-bridge/plugin/WorldGeometry.cs) 把静态三角表面保守采样成附近 MC barrier 格子；[MinecraftBridge.StreamGround](../source/cs2-bridge/plugin/MinecraftBridge.cs) 分批发送这些不可见支撑，限制每 tick 工作量。原生地形缓存必须与预切地图一致，否则桥接会把已经挖掉的地板重新补成不可见支撑。

早期 Dust2 独立实验还在 [TerrainLab.MutableTerrainCell](../source/cs2-bridge/plugin/TerrainLab.cs) 中显式跳过 `de_dust2_mccs` 的 x=`7..8`、z=`-63..-62`、y=`90..93`，防止采样误差重新封洞。这个例外只覆盖该旧实验，并非任意地图通用的可破坏区域表。

MC 真实方块由 [WorldBridge.onBlockChanged / flush](../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/WorldBridge.java) 在邻居更新后读取碰撞 AABB，传给 CS。门、栅栏、活塞等不能用整方块近似，所以 [TileCollisionBoxes](../source/cs2-bridge/plugin/BlockColliders.cs) 将每个 AABB 细分为立方体，末端重叠填满而不越出边界。CS 在最终位置生成 `prop_physics_override`，设置模型尺度、不可见状态和 `DisableMotion`。

当前预算是每方块最多 128 个 CS 碰撞体、总计最多 2048 个；MC 单元形状、消息大小和每 tick 发送量也有限制。超过预算会拒绝更新/记录警告，不能将复杂建筑中的缺碰撞直接归因于坐标误差。

[BuildingMotion.TickBuildingMotion](../source/cs2-bridge/plugin/BuildingMotion.cs) 当前只做测试诊断采样。`sneakEdgeAssist` 与 `underfootAssist` 都是 false；保留的 [BuildingMotionMath](../source/cs2-bridge/plugin/BuildingMotionMath.cs) 和检查项目不意味着运行时启用了防掉落或自动抬升。

## 4. Inferno 覆盖、C4 爆炸和黑曜石规则

[MinecraftBridge.Tick](../source/cs2-bridge/plugin/MinecraftBridge.cs) 收到 MC `hello` 且当前地图名恰好是 `de_inferno` 时，会发送：

```text
fill 4 98 -75 14 99 -64 minecraft:dirt
fill 48 98 -17 59 99 -5 minecraft:dirt
```

这是握手触发，不是每回合自动恢复；`de_inferno_mccs` 不满足这条具体判断，尽管它在受支持地图列表中。使用克隆地图名时需另外安排填充，不要以为改一个启动参数就能得到相同联动。

[TrackPlantedC4](../source/cs2-bridge/plugin/ObsidianBlastShield.cs) 持续保存仍有效的 `planted_c4` 坐标，避免爆炸事件到达时实体已经消失。`EnsureC4TestObservers` 虽然带 Test 名称，但在正常插件加载时也会注册一次 `EventBombExploded`；该事件调用 `SpawnMinecraftTnt`。

`SpawnMinecraftTnt` 当前行为是：

- CS XY 落在 B 区范围：先把整个 B 填充范围设为空气，再在 MC `(9,101,-70)` 召唤短引信 TNT。
- CS XY 落在 A 区范围：先把整个 A 填充范围设为空气，再在 MC `(50,101,-11)` 召唤短引信 TNT。
- 其他位置：换算炸弹坐标，在中心及周围召唤 7 个 TNT，使用不同引信。

所以 A/B 洞口是区域清空效果，不是按 C4 球形爆炸半径逐块挖土。当前函数只检查 X/Y，没有地图名或 Z 校验；这是仍需改进的场景约束，不能认定任意地图相同 XY 都应触发 Inferno 行为。

MC 的 [ServerExplosionMixin](../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/mixin/ServerExplosionMixin.java) 向桥接报告爆炸。当前 CS `HandleIncoming` 对 `explosion` 只记录观察，因为伤害已经通过 MC 代理/伤害链处理；不会再无条件叠加一个原生爆炸伤害。

另一个独立功能是黑曜石挡 C4：[TryBlockC4Blast / ObsidianBlocksBlast / SegmentEntersBlastCube](../source/cs2-bridge/plugin/ObsidianBlastShield.cs) 只处理原生 `planted_c4` 的 blast 伤害，从炸弹到玩家躯干作 MC 格子 DDA 追踪。射线真正进入黑曜石或哭泣的黑曜石立方体内部才置伤害为零，擦边/擦角不算；最多遍历 4096 格。它既不是所有爆炸通用护盾，也不会取消前述 `fill ... air` 命令。

## 5. 运行时怎么验收，哪些测试不能混用

先完成基础连接，再在离线、可恢复的实验地图和存档中验收。需要记下当前地图、资源版本/散列、`world.tri` 来源、角色位置、MC 方块坐标和实际观察结果；只留下命令已发送的日志不足以判断成功。

1. **覆盖前**：冷启动预切地图，确认洞口可见，玩家能进入对应空间。若仍被原生地板挡住，先检查物理资源，不要在运行时用传送掩盖问题。
2. **覆盖后**：填上泥土，检查 MC 方块状态、CS 碰撞体数量及站立高度。方块可见而角色穿过，检查 `blocks` 更新与预算；角色踩在看不见的平面上，检查原生物理/旧 barrier。
3. **普通破坏**：手动挖一块或让 MC TNT 破坏一块，观察该块的形状清空和 CS 实体删除，不要一次只测试整片 `fill air`。
4. **C4 联动**：真实安放 C4，经历原生倒计时和 `bomb_exploded`，检查正确区域变化；再检查区外 C4 不应错误清理 A/B。最后恢复填充并重测。
5. **黑曜石护盾**：同一原生 C4、同一玩家位置比较无遮挡/实穿黑曜石/仅擦边三组；区分护盾计数、原生伤害和 MC TNT 后续伤害。
6. **生命周期**：退出重进、原图→实验图→原图、重连 MC 后检查地图缓存、填充状态和碰撞重新同步，不能只验一次热加载。

可以先用 BridgeControl 读取地图和 MC 方块状态。以下命令只请求诊断，依赖 relay、MC 和插件已经连接，且本地令牌已配置：

```powershell
New-Item -ItemType Directory -Force "$env:MCCS_ROOT/temp" | Out-Null
$TerrainInspect = @(
  @{t='director';host='cs2';op='inspect'},
  @{t='uistate';requestId='terrain-check'},
  @{t='blockinspect';p=@(9,98,-70,50,98,-11)},
  @{wait_ms=500}
)
$TerrainInspectPath = "$env:MCCS_ROOT/temp/terrain-inspect.json"
[IO.File]::WriteAllText($TerrainInspectPath, ($TerrainInspect | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
dotnet run --project source/cs2-bridge/tools/BridgeControl/BridgeControl.csproj -c Release -- `
  $TerrainInspectPath "$env:MCCS_ROOT/temp/terrain-inspect-response.json"
```

CS `inspect` 写入本地 `logs/cs2-live-state.json`，MC 响应写入指定 capture。核对响应是本次新生成的，并检查 `map`、连接状态、`validBlocks`、`blockBodies` 与 `blockinspect` 的 `loaded/state`。BridgeControl 输出 `Sent local command` 只表示发送完成，不代表游戏达到了目标状态。

测试专用 director 接口还包括 `test_blast`、`test_c4_prepare`、`test_c4`、`test_c4_finish`。前者直接调用 MC 爆炸联动，传入 **CS 坐标**；`test_c4` 使用 **MC 坐标**创建原生测试炸弹，要求活动回合且不在热身/冻结/暂停。测试准备会临时修改回合 convar，必须走 finish 恢复。它们不能替代实际安包触发验收，也不宜未经确认直接用于已有拍摄存档。

## 6. 可复用的检查代码及当前证据边界

| 检查入口 | 它验证什么 | 它不能证明什么 |
|---|---|---|
| `TriangleClipper.RunSelfTests` | 合成三角形的盒子减法、面积与方向 | 当前 CLI 不会自动调用它；源码中存在自测函数不等于本次已执行 |
| `PhysicsMeshCutter.ValidateBvh` | 三角网格 BVH 结构和覆盖有效 | 游戏一定接受所有修改资源 |
| `Kv3V4Writer.AssertEquivalent` | 序列化后逻辑 KV3 树等价 | 光照、导航、材质和运行时行为正确 |
| `MapNamespaceBuilder.Build` | 派生包条目、校验和、读取字节一致 | 安装成功、旧缓存无污染 |
| `MapCutRender.VerifyPatch` | 索引退化范围、重解析及未改块一致 | 边界大三角形已精确裁开 |
| [HostGeometryCheck.java](../tools/HostGeometryCheck.java) | Java 射线命中与传入的 C# TSV 参考结果一致，位置误差不超过 0.03 CS 单位 | 参考数据所对应地图之外的几何正确 |
| [BuildingMotionChecks](../source/cs2-bridge/tools/BuildingMotionChecks/Program.cs) | 保留的运动数学函数 | 当前运行时启用了这些辅助功能 |

HostGeometryCheck 可以独立编译，不需要复制整套游戏：

```powershell
New-Item -ItemType Directory -Force "$env:MCCS_ROOT/temp/geometry-check-classes" | Out-Null
javac -d "$env:MCCS_ROOT/temp/geometry-check-classes" `
  source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/HostGeometry.java `
  tools/HostGeometryCheck.java
# TSV 必须先由本机相同地图的参考程序生成：每行 startXYZ、endXYZ、hit(0/1)、hitXYZ，共10列，以制表符分隔。
java -cp "$env:MCCS_ROOT/temp/geometry-check-classes" HostGeometryCheck `
  "$env:MCCS_ROOT/build-cache/map-physics/de_dust2/world.tri" `
  "$env:MCCS_ROOT/temp/arrow-geometry-fixtures.tsv"
```

[GeometryChecks 的 `--arrow-fixtures`](../source/cs2-bridge/tools/GeometryChecks/Program.cs) 能生成 500 条确定性参考射线，但该历史诊断工具仍有开发目录和 Dust2 约定；换电脑先核对其路径，不要把报告或 TSV 当成随仓库附带的数据。成功时 HostGeometryCheck 输出 `pass:true` 以及实际射线数、命中数和最大误差；缺少输入时不能宣称该检查已通过。

地图工具没有重烘焙光照/PVS、重建导航网格、生成原生坑壁或坑底，也没有实现任意原生凸包切割。旧 Dust2 小坑用 MC 基岩补四壁和底座，这不自动覆盖 Inferno。当前文档依据代码核对；本次公开整理没有启动游戏重做端到端地形、C4 或建筑验收。

公开版路径修改已在 .NET 10 下构建通过（0 警告、0 错误），并用不含游戏资源的临时目录检查了：无环境变量时 `--help` 可用；缺失/相对 `MCCS_ROOT` 被拒绝；新的绝对工作区能完成空输入 `--dry-run` 并写报告；`build-cache` 同级目录和 `games` 输出目录被拒绝且没有创建目标。这里验证的是工具路径行为，空输入检查不属于地图裁切验收。
