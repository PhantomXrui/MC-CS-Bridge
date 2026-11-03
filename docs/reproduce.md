# 换一台 Windows 电脑：按检查点重建

这份手册把准备环境、编译、安装、启动和验收分开。每一步留下结果，后续 Codex 只从失败的步骤继续，不重新探索整个项目。功能的实际调用过程见 [玩法](features-gameplay.md)、[渲染与 F8](features-rendering.md)、[地形](features-terrain.md)。

**当前不能保证另一台电脑百分百复刻历史演示。** 公开仓库是源码，不含游戏、派生地图数据、模型识别表、语音或 Demo；Fabric Loom 仍为快照依赖，尚无干净新机端到端验收。本手册提供可以执行和判断的步骤，并标出尚需人工补齐的输入，不用“编译通过”代替“效果一样”。

## 0. 先固定复刻对象

最小目标是：Dust2 离线地图中，MC 画面合成、相机同步和一个方块的放置/移除能够运行。先完成这个目标，再启用玩法、改图或 F8。不要一开始同时切换地图版本、更新依赖和修复镜头。

```powershell
git clone https://github.com/PhantomXrui/MC-CS-Bridge.git
Set-Location MC-CS-Bridge
git rev-parse HEAD
$env:MCCS_ROOT = (Get-Location).Path
$env:MC_CS_YOFFSET = '96'
foreach ($name in 'runtime','logs','temp','build-cache') {
    [IO.Directory]::CreateDirectory((Join-Path $env:MCCS_ROOT $name)) | Out-Null
}
```

把提交号记到自己的 `logs/reproduction-notes.md`。以后“同一版”指此提交号，不能只说 main。以上环境变量只影响当前 PowerShell 及它新启动的子进程；已运行的游戏和启动器不会自动获得它们。

### 版本基线（2026-11-04 从本地文件读取）

| 项目 | 本地基线 | 新机需要做什么 |
|---|---|---|
| OS / 图形 | Windows x64；D3D11 路径 | 记录系统版本、GPU 和驱动版本；不要假定 Vulkan 路径等价 |
| CS2 | `PatchVersion=1.40.6.6`，Client/Server `2000481`，2025-02-03 | 使用自己可合法使用的匹配版本；记录 `csgo/steam.inf` 内容及文件哈希 |
| CounterStrikeSharp API | 文件版本 `1.0.298.0`，产品 `1.0.298+f05cc5e` | 插件引用与实际服务端安装保持一致；API 版本不等于已确定整个 Metamod 安装版本 |
| Minecraft / Fabric | MC 26.3，Loader 0.19.5，API 0.161.0+26.3 | 新建独立测试实例，避免混入其他模组 |
| Java | Temurin 25.0.4.1+1 | `java -version` 确认当前实际使用的 Java |
| Gradle / Loom | 9.7.1 / 1.18-SNAPSHOT | 快照尚未锁定不可变制品；记录实际解析版本及哈希 |
| .NET SDK | 8.0.426、10.0.402 | .NET 8 编译运行插件/relay；部分资源工具需要 .NET 10 |
| MSVC / Windows SDK | x64 C++20；本地 SDK 包 10.0.26100.6584 | 安装 C++ 编译工具；记录 `cl` 实际版本，仓库未锁定完整编译器包 |
| ReShade | 本地 addon API 20 | API 头文件、运行时、官方 FX 头文件分别准备；运行时版本仍需记录 |
| Python | 3.13.15 | 辅助脚本主要用标准库，不能用 Python 代替 Java/.NET 编译器 |
| VRF | MapPatchLab 10.2.3850；MapCutRender 12.0.4261 | 服从各 `.csproj` 固定版本，不能统一升级 |

源码版本配置是最终依据：[Gradle 属性](../source/universal-modder/examples/minecraft-gta5-passthrough/mc/gradle.properties)、[插件项目](../source/cs2-bridge/plugin/MinecraftBridge.csproj)。如果基线依赖已无法取得，先登记为阻塞，不把随意替换后的结果叫作完全复刻。

## 1. 准备目录和本地令牌

推荐将 clone 根目录同时作为 `MCCS_ROOT`。运行组件的输出都以它为基准：

```text
MC-CS-Bridge/
  source/                     已公开源码
  config/actor-interactions.json
  runtime/bridge-token.txt     本机生成，不提交
  build-cache/map-physics/de_dust2/world.tri
  logs/                       每阶段的检查结果，不提交
  temp/                       临时控制消息，不提交
  games/Counter-Strike 2/csgo/  部分地图工具约定的自有资源布局
```

游戏可安装在别处，但使用固定布局的提取工具时必须提供约定位置的自有资源副本，或明确修改工具输入路径。不要为了满足旧脚本创建整盘 D→E 映射。旧机器启动器、账号配置、私人存档不属于移植接口。

以下命令只在令牌缺失时生成 32 字节随机值，不显示令牌：

```powershell
$tokenPath = Join-Path $env:MCCS_ROOT 'runtime/bridge-token.txt'
if (-not (Test-Path -LiteralPath $tokenPath)) {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $secret = [BitConverter]::ToString($bytes).Replace('-','').ToLowerInvariant()
    [IO.File]::WriteAllText($tokenPath,$secret,[Text.UTF8Encoding]::new($false))
    Remove-Variable secret,bytes
}
```

验收：文件存在且有内容；不把内容写入日志、问题报告或提交。MC 的 `passthrough.token` 与此文件内容相同。只改 MC 的端口不够：当前 relay 和 BridgeControl 都固定连 `127.0.0.1:25599`，初次复刻保持默认。

## 2. 先构建不依赖游戏运行的组件

每条命令结束后检查 `$LASTEXITCODE`，非 0 就停在该阶段。以下命令默认在仓库根目录执行。

```powershell
dotnet --list-sdks
dotnet build source/cs2-bridge/relay/BridgeRelay.csproj -c Release
dotnet build source/cs2-bridge/tools/BridgeControl/BridgeControl.csproj -c Debug
```

预期分别得到 `source/cs2-bridge/relay/bin/Release/net8.0/BridgeRelay.dll` 和 `source/cs2-bridge/tools/BridgeControl/bin/Debug/net8.0/BridgeControl.dll`。保留 Debug 路径是因为现有 live 测试脚本按这个位置调用 BridgeControl。

插件需要外部 API 目录，同时含 `CounterStrikeSharp.API.dll`、`Microsoft.Extensions.Logging.Abstractions.dll`：

```powershell
$cssApi = 'C:\Your-CS2\csgo\addons\counterstrikesharp\api' # 替换成本机真实位置
dotnet build source/cs2-bridge/plugin/MinecraftBridge.csproj -c Release "-p:CounterStrikeSharpApiDir=$cssApi"
```

预期输出 `source/cs2-bridge/plugin/bin/Release/net8.0/MinecraftBridge.dll`。只有 .NET runtime 没有 SDK 时，无法执行 build；NU1301 常是包源/网络问题，应先解决依赖还原，不要因此改玩法代码。

在 **x64 Native Tools Command Prompt for Visual Studio** 中构建合成器：

```bat
set "ReShadeIncludeDir=C:\Your-SDKs\reshade\include"
set "MCCS_ENABLE_EXPERIMENTAL_DANCE="
pushd .
call source\cs2-bridge\native\build-msvc.cmd
popd
call tests\rendering\run-readback-check.cmd
```

预期得到 `source/cs2-bridge/build/MinecraftCS2.addon64`；GPU 队列测试输出 `PASS 80 mixed ...`。测试只检查人工构造的数据，不证明真实游戏 F8 画面正确。

## 3. 构建 MC 模组

```powershell
java -version
gradle --version
Push-Location source/universal-modder/examples/minecraft-gta5-passthrough/mc
try { gradle build } finally { Pop-Location }
```

仓库不附 Gradle wrapper JAR。可安装 Gradle 9.7.1，或在目录内用它执行 `gradle wrapper` 后再用 `gradlew.bat`；单有 wrapper 属性文件不能运行构建。检查 `build/libs` 中生成的模组 JAR，使用正常运行制品，不使用 sources JAR。

本次公开整理只完成过“64 个 Java 文件配本机依赖的直接编译”；Gradle 在原机出现 daemon 回环连接失败，**这里的新机打包命令尚未做干净环境验证**。若失败，保存 Gradle 错误与 Java/Gradle 版本，先排查回环连接、依赖解析及源集配置。不要用旧 JAR 冒充当前源码构建成功。

## 4. 为地图生成碰撞输入

普通合成/碰撞验证先用原图，不需要先切坑。`extract-map-physics-any.py` 读取约定游戏目录；仅支持资源内联存于当前 VPK 的情况，外部分卷会明确失败。

```powershell
python tools/extract-map-physics-any.py de_dust2 "$env:MCCS_ROOT"
dotnet build source/cs2-bridge/tools/MapExport/MapExport.csproj -c Release
$physicsDir = Join-Path $env:MCCS_ROOT 'build-cache/map-physics/de_dust2'
Get-ChildItem -LiteralPath $physicsDir -Recurse -Filter '*.vphys_c'
```

从输出确认地图主物理 `world_physics.vphys_c` 的真实路径，不要任选其他条目。然后执行：

```powershell
$vphys = Join-Path $physicsDir 'world_physics.vphys_c' # 以实际提取结果为准
$tri = Join-Path $physicsDir 'world.tri'
dotnet run --project source/cs2-bridge/tools/MapExport/MapExport.csproj -c Release -- "$vphys" "$tri"
```

验收：工具输出正数三角形数量；`world.tri` 开头是 little-endian `0x3152544d` 与数量，每个三角形 9 个 float，文件总长应为 `8 + 数量 × 36`。CS2 插件读取 `MCCS_ROOT/build-cache/map-physics/<当前地图>/world.tri`，MC 配置指向同一个文件。地图名、版本和几何缓存必须对应。

## 5. 安装到自己的离线测试实例

1. 在匹配的 Minecraft + Fabric 实例中安装 Fabric API 及本次模组 JAR；用独立游戏目录和存档。
2. 安装匹配的 Metamod/CounterStrikeSharp，把本次插件输出安装到 `csgo/addons/counterstrikesharp/plugins/MinecraftBridge/`。根据项目运行依赖安装，避免把旧 DLL 混在同名目录。
3. 给本地 CS2 D3D11 安装支持 addon 的 ReShade；把 `MinecraftCS2.addon64` 放入它的 addon 搜索位置。把 `source/cs2-bridge/shaders/MCPassthrough.fx` 放入效果搜索目录，并配置官方 `ReShade.fxh` 的 include 目录。
4. 检查 ReShade 日志确认 addon 加载，效果无编译错误。`reshade.hpp` 是编译 addon 的头文件，`ReShade.fxh` 是编译 FX 的头文件，二者不能互相替代。
5. 在 MC 启动器的 **JVM 参数** 中设置以下项目。路径及令牌替换为本机值；不是在游戏聊天框输入。

```text
--enable-native-access=ALL-UNNAMED
-Dpassthrough.port=25599
-Dpassthrough.token=<本机 runtime/bridge-token.txt 内容>
-Dpassthrough.world=passthrough_de_dust2
-Dpassthrough.hostGeometry=<绝对路径>/build-cache/map-physics/de_dust2/world.tri
-Dpassthrough.yOffset=96
-Dpassthrough.vanillaCombat=true
-Dpassthrough.actorInteractionsConfig=<绝对路径>/config/actor-interactions.json
```

带空格的路径按启动器规则引用为单个参数。不要复制含令牌的完整启动命令到公开 issue。MC 会准备专用空世界及初始物品；此模组会配置这个世界，所以不要让它进入珍贵存档。

## 6. 按顺序启动和检查连接

所有组件以同一 Windows 用户运行，只开一套桥接。共享内存和命名管道名称是固定的，第二套会发生冲突。

1. 从已设置 `MCCS_ROOT` 的环境启动 MC 实例，等待专用世界进入。日志应出现 `host link listening on 127.0.0.1:25599`。
2. 在同一环境启动 relay：

   ```powershell
   dotnet source/cs2-bridge/relay/bin/Release/net8.0/BridgeRelay.dll
   ```

   relay 起初等待 CS2 命名管道连接是正常现象。该命令留在一个单独终端运行。
3. 通过自己的本地离线游戏启动入口传入 `-insecure +sv_lan 1 +map de_dust2`。不提供账号或认证替代组件；原机专用启动器不在公开仓库中。插件显式要求 `-insecure`。
4. relay 出现 `connected`，MC 出现 `host connected` 后，再查看渲染。缺连接时先不要调 shader。
5. 可运行 `python tools/read-bridge-memory.py`，共享区 magic 应为 `0x504d5343`。该脚本只打开已有映射，不创建映射；“找不到”说明生产组件尚未创建它。

对 CS2 插件发送一次只读 inspect，检查当前状态：

```powershell
$inspectPath = Join-Path $env:MCCS_ROOT 'temp/inspect.json'
[IO.File]::WriteAllText($inspectPath,'[{"t":"director","host":"cs2","op":"inspect"}]',[Text.UTF8Encoding]::new($false))
$requestedAt = [DateTime]::UtcNow
dotnet source/cs2-bridge/tools/BridgeControl/bin/Debug/net8.0/BridgeControl.dll "$inspectPath"
if ($LASTEXITCODE -ne 0) { throw 'BridgeControl failed' }
$statePath = Join-Path $env:MCCS_ROOT 'logs/cs2-live-state.json'
$deadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    $stateFile = Get-Item -LiteralPath $statePath -ErrorAction SilentlyContinue
    if ($stateFile -and $stateFile.LastWriteTimeUtc -ge $requestedAt) { break }
    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $deadline)
if (-not $stateFile -or $stateFile.LastWriteTimeUtc -lt $requestedAt) { throw 'No fresh inspect result' }
Get-Content -LiteralPath $statePath -Raw
```

状态中核对连接、MC ready 和当前地图。几何数量不在这个 JSON 中：另看本次 `logs/cs2-bridge.log` 的 `Loaded map triangles=<正数>`，以及 MC 日志的 `Host terrain de_dust2:`。核对日志时间，避免读到上次运行留下的结果。BridgeControl 返回不等于所有异步玩法已完成，需看相应 ack/状态。

## 7. 最小画面与碰撞验收

| 顺序 | 操作 | 通过条件 |
|---|---|---|
| A | 原地转动视角，移动一小段 | 两端位置/视角一致，没有持续漂移或旧输入卡住 |
| B | 地面上放置一个方块，再移除 | 画面、CS 碰撞和 MC 状态一起变化；不只是颜色消失 |
| C | 站到方块前后观察 | 前后遮挡合理，HUD 不被三维深度错误裁掉 |
| D | 打开菜单、切焦点、正常返回 | 输入释放和恢复，鼠标/键盘不残留按下状态 |
| E | 正常退出一端，再重新启动 | 不使用过期帧/输入；重新握手与方块同步完成 |
| F | 在相同位置再次放置/移除 | 恢复后仍可操作，说明不是首次启动偶然成功 |

把每项的提交号、版本、输入、观察、日志时间和截图保存在本机。图片应能证明该项结果，而不是只拍一张看起来正常的画面。详细脚本见 [测试说明](testing.md)。

## 8. 再启用可选功能

- 成年干员、金苹果、小 T、语音和伤害：按 [玩法实现](features-gameplay.md) 分开验收。语音缺失与逻辑消息失败不是同一个故障；四次生成请求不代表必定出现四个小 T。
- Inferno 预切坑：按 [地形实现](features-terrain.md) 在备份/副本中生成物理和渲染，再核对相同切割盒。不能把任意地图套进 Inferno 坐标分区逻辑。
- F8：按 [渲染实现](features-rendering.md) 准备与游戏版本匹配的本地生成表和 shader 缓存；默认公开构建明确不可用。补齐后再启用构建宏，测试开关、暂停回放、换人、手套/C4、黑影和帧耗时。
- 第三人称鼠标环绕：单独列为验收项，当前不能认定“第三人称已切换”就等于用户要的绕人物拍摄已完成。

## 9. 常见失败：一次只查一层

| 症状 | 最先检查 | 不该直接推断 |
|---|---|---|
| `MCCS_ROOT` 报错 | 绝对路径、是否由设置过变量的父进程启动 | 不等于插件 API 不兼容 |
| WebSocket 1008 | MC 参数与本地令牌一致、非空 | 不要关闭认证来掩盖配置问题 |
| relay 一直等待 | 插件是否加载、`-insecure`、同一 Windows 用户 | 不必先修改端口或防火墙 |
| 有画面没碰撞 | 地图名/tri 路径、几何加载、blocksync 是否完成 | 不等于 ReShade 失效 |
| 地面浮起/陷入 | 三端坐标比例与 yOffset，当前静态转换仍有固定 96 | 不要只改 MC 一个偏移 |
| F8 无反应 | 是否默认禁用构建、所需表/缓存是否匹配 | 不要不停强制触发未知 shader |
| 有黑影/手套飞离 | 同一人物的颜色、深度、遮罩、附件路径 | 身体变形正常不能作为整体通过 |
| Gradle daemon 连接失败 | 本机回环、Java 版本、Gradle 日志 | javac 通过不代表 Gradle 已打包 |
| GPU/游戏卡顿 | 有界读回统计、缓存命中、帧耗时 | 不把 WARP 的耗时当真实游戏 FPS |

## 10. 何时才能说完整复刻

需要固定提交与依赖制品、记录外部游戏/地图/API 版本及哈希、补齐本地生成输入，在全新环境完成基础和可选功能验收，再保存可重复执行的安装入口。当前缺少的主要环节是干净 Gradle 打包、完整游戏安装/启动自动化、F8 版本相关数据准备与实机全覆盖，以及未完成的玩法/镜头问题。请按检查点记录这些缺口，不把它们藏进“运行一下即可”。
