# 构建与复现

首次换电脑请从 [逐阶段重建手册](reproduce.md) 开始；本文是组件级构建参考。具体功能调用链见 [玩法](features-gameplay.md)、[渲染](features-rendering.md)、[地形](features-terrain.md)。

这份源码的历史开发环境为 Windows x64。它依赖两个游戏、服务端模组 API、Fabric 和 ReShade；游戏文件及私有运行资料不包含在公开源码中。

## 已记录依赖

| 组件 | 开发环境版本或要求 |
|---|---|
| Minecraft Java | 26.3 |
| Java | JDK 25，模组编译目标 Java 25 |
| Fabric Loader | 0.19.5 |
| Fabric API | 0.161.0+26.3 |
| Fabric Loom | 1.18-SNAPSHOT，快照依赖可能随时间改变 |
| Gradle | 原 wrapper 指向 9.7.1 |
| Java-WebSocket | 1.6.0 |
| CS2 插件、relay | .NET 8 目标框架 |
| 地图处理工具 | 部分项目目标 .NET 10 |
| CS2 插件接口 | CounterStrikeSharp.API；须与本地服务端匹配 |
| 原生渲染 | MSVC x64、C++20、Windows SDK、D3D11、ReShade addon API |
| MapPatchLab | ValveResourceFormat 10.2.3850 |
| MapCutRender | ValveResourceFormat 12.0.4261 |

两个地图工具使用不同 VRF 版本，是其资源格式与 API 兼容性要求，不应未经验证统一升级。

## 公开版的路径配置

- 运行 CS2 插件与 relay 前设置环境变量 `MCCS_ROOT` 为本地工作区绝对路径；未设置时明确报错。工作区需有 `runtime/bridge-token.txt`（自己生成，不能上传）、`logs` 和本地地图缓存。
- 插件构建参数 `-p:CounterStrikeSharpApiDir="你的 API 目录"` 指向同时含 `CounterStrikeSharp.API.dll` 和 `Microsoft.Extensions.Logging.Abstractions.dll` 的目录。
- 在 Visual Studio 的 **x64 Native Tools Command Prompt** 中运行 `native/build-msvc.cmd`。设置 `ReShadeIncludeDir` 指向包含 `reshade.hpp` 的 ReShade SDK include 目录；本地验证为 addon API 20。SDK 头文件和运行库不随本仓库提供。
- 公开版默认不编译 F8 实验模块。只有补齐所需本地生成文件，才设置 `MCCS_ENABLE_EXPERIMENTAL_DANCE=1`。
- MC 的 `passthrough.actorInteractionsConfig` 可以覆盖互动配置路径；地图工具、生成器和测试脚本仍有开发目录约定，运行前需要检查，不会自动修改你的游戏。

## 组件构建顺序

1. 准备合法持有的游戏及兼容的 Fabric、Metamod/CounterStrikeSharp、ReShade addon 环境。
2. 用 JDK 25 构建 MC 模组：进入 `source/universal-modder/examples/minecraft-gta5-passthrough/mc`，使用已安装的 Gradle 9.7.1 执行 `gradle build`（或 `gradle wrapper` 生成本机 wrapper 后使用 `gradlew.bat build`；仓库不附 wrapper JAR）。需要可获取的 Gradle 与 Maven 依赖；输出通常位于 `build/libs`。
3. 配置 CounterStrikeSharp API 引用，执行 `dotnet build source/cs2-bridge/plugin/MinecraftBridge.csproj -c Release -p:CounterStrikeSharpApiDir="你的 API 目录"`。
4. 执行 `dotnet build source/cs2-bridge/relay/BridgeRelay.csproj -c Release`。
5. 用 MSVC x64 构建原生 addon。不要用 GNU ABI 代替 MSVC，ReShade C++ 接口不保证 ABI 兼容。
6. 地图工具按各自项目独立 `dotnet build`，在使用者本地提取和生成地图缓存。

公开版默认禁用原生 F8 实验模块：它需要与目标版本对应的网格识别表及预编译变换着色器缓存，而这些本地生成数据没有随源码提供。先构建基础桥接；只有自行准备匹配资源并完成离线校验后，才启用该可选模块。不能把“源码编译成功”当成模型动作已经可用。

`tools/build-dance-shader-cache.cpp` 接受原始着色器目录与输出头文件路径，只在离线工具中进行反射与编译。它不替代游戏内采集或授权资源准备；原始游戏着色器及反汇编文件不随仓库发布。

## 本地连接配置

MC 的 `passthrough.port` 默认为 25599，`passthrough.token` 必须与 relay 本地令牌一致。MC 使用 Java 外部函数接口，需要按启动环境配置 `--enable-native-access=ALL-UNNAMED`。几何文件通过 `passthrough.hostGeometry` 指定，垂直偏移通过 `passthrough.yOffset` 指定；CS2 端还应核对 `MC_CS_YOFFSET` 和固定坐标换算逻辑。

CS2 插件要求显式 `-insecure` 本地环境，复现启动使用 `-insecure +sv_lan 1`。游戏启动、账户授权及资源安装由使用者自行完成；本仓库不提供游戏客户端或认证替代组件。

## 最小验收

- relay 与 MC 建立本地连接，令牌错误时拒绝连接。
- 相机、位置和输入状态同步，失联后停止使用过期输入。
- 放置一个 MC 方块，核对可见性、遮挡和两端碰撞。
- 切换地图及退出一端，核对恢复流程。
- 单独验证 F8 ON/OFF、暂停 Demo、换人物、手套和背部附件。
- 第三人称镜头绕人物旋转与跟随移动单独验证。

既有 WARP GPU 测试和历史实机日志只能证明其具体输入与观察场景，不代表全版本、全模型兼容；警方黑影与镜头操作仍应作为验收重点。

## 安装位置与外部效果依赖

构建产物需由使用者安装到自己的离线环境：Fabric JAR 放入 Minecraft 的 `mods`；插件 DLL 及依赖放入 CounterStrikeSharp 的 `plugins/MinecraftBridge`；原生 `MinecraftCS2.addon64` 放到 ReShade 的 addon 搜索位置，`shaders/MCPassthrough.fx` 放到效果搜索目录。FX 还需要 ReShade 官方效果头文件 `ReShade.fxh`，它与 addon API 的 `reshade.hpp` 不是同一个文件，均需自行获取。游戏路径因发行和安装布局不同而异，不提供覆盖游戏目录的自动安装脚本。

## 本次公开副本验证（2026-11-04）

- relay：.NET 构建通过，0 个警告/错误。
- CounterStrikeSharp 插件：构建通过，仅 Windows 平台 API 分析警告。
- 默认原生合成器：MSVC 构建通过，只有重复宏定义警告；F8 默认为未编译。
- MC 64 个 Java 源文件：Java 25 配合本机现有依赖直接编译通过；Gradle 守护进程在本机回环连接失败，**未完成干净环境的 Gradle 打包验证**。

以上是组件编译检查，不代表新机器完整安装、两款游戏端到端运行或未完成玩法验收已通过。
