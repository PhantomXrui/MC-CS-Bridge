# 测试入口、前提与结果解释

这份文件区分离线验证与会修改游戏世界的 live 测试。源码公开不等于每个历史测试在当前版本都重新通过。

## 不启动游戏的检查

| 检查 | 命令/入口 | 能证明什么 |
|---|---|---|
| 插件、relay、MC、addon 构建 | [构建说明](build.md) | 对指定工具链能编译；不证明游戏效果 |
| T 模型分配算法 | `dotnet run --project source/cs2-bridge/tools/UniqueTAllocationChecks` | 分配逻辑的断言；不加载真实模型 |
| 历史移动辅助数学 | `dotnet run --project source/cs2-bridge/tools/BuildingMotionChecks` | 留存数学辅助算法；当前 runtime 并未启用全部辅助功能 |
| GPU 异步读回上限 | `tests/rendering/run-readback-check.cmd` | D3D11 WARP 合成数据读回正确且有界 |
| 几何对照 | `tools/HostGeometryCheck.java` | 需要本机 tri 与 TSV 射线样本，比较 MC 几何实现与预期 |

GPU 测试使用 80 个合成缓冲区，交替请求 4 KiB/512 KiB，检查结果内容、最多 64 个 pending、2 MiB 总排队数据、每帧最多 8 次提交，20 秒超时。测试自身用 `Flush` 提交模拟帧，生产读回队列不因此改为同步刷新。输出 longest call 只是本机这次调用耗时，不能作为真实游戏性能保证。

```bat
rem 在 MSVC x64 开发者命令行内，从仓库根目录执行
call tests\rendering\run-readback-check.cmd
```

产物在忽略的 `build-cache/public-tests`。不需要游戏、原始 shader 或人物识别表。

## Live 测试的共同前提

这些脚本会移动玩家、修改方块、生成或伤害实体、调整测试模式，部分操作不会完全还原。仅在独立测试存档、对应本地离线地图中使用；不能把“finally 中有恢复”当作整个世界无变化的保证。

1. 按 [新机手册](reproduce.md) 完成基础桥接。
2. 地图/坐标/物品槽与脚本一致。许多历史脚本针对 Dust2 与 yOffset=96，其他地图需要修改场景数据。
3. 设置 `$env:MCCS_ROOT=(Get-Location).Path`，构建 Debug BridgeControl：

   ```powershell
   dotnet build source/cs2-bridge/tools/BridgeControl/BridgeControl.csproj -c Debug
   ```

4. MC、relay、CS2 都已连接，玩家状态符合该脚本条件。
5. 每次只运行一个脚本。不要与手动操作、另一个桥接实例并发。

## 已公开的 7 个场景脚本

| 文件 | 实际测试对象 | 结果位置（MCCS_ROOT 下） |
|---|---|---|
| [test-bridge-interactions.ps1](../tools/test-bridge-interactions.ps1) | 墙/开口碰撞、真实按键放置与破坏；名称虽有 interactions，**不是金苹果完整验收** | `logs/interaction-test-result.json` |
| [test-bridge-damage.ps1](../tools/test-bridge-damage.ps1) | TNT 等伤害路径对 bot 血量/存活影响 | `logs/damage-test-result.json` |
| [test-vanilla-combat.ps1](../tools/test-vanilla-combat.ps1) | MC 原版武器/实体战斗场景，可选分组 | `logs/combat-v023-*.json` |
| [test-block-placement-v023.ps1](../tools/test-block-placement-v023.ps1) | 放置/移除多个循环 | `logs/block-placement-v023.json` |
| [test-block-player-v023.ps1](../tools/test-block-player-v023.ps1) | CS 玩家对实际放置方块的碰撞 | `logs/block-player-collision-v023.json` |
| [test-building-catalog-v024.ps1](../tools/test-building-catalog-v024.ps1) | 多类建筑方块的状态/碰撞 | `logs/building-catalog-v024.json` |
| [test-arrow-terrain-v025.ps1](../tools/test-arrow-terrain-v025.ps1) | 箭与地形的相交/贴附 | `logs/arrow-terrain-v025.json` |

例如，在场景条件准备好后，从根目录执行 `powershell -NoProfile -File tools/test-block-placement-v023.ps1`。脚本文件的参数和场景是最终依据，先读前几十行及操作列表。读取结果中的各断言、观察值和本次时间，不能只看进程退出码；部分脚本记录失败布尔值但不抛异常。

## 只读运行诊断

```powershell
python tools/read-bridge-memory.py
python tools/read-pawn-dance-diagnostics.py
python tools/sample-dance-frame-time.py
```

三个脚本依赖已存在的共享区，通过 [shared_map.py](../tools/shared_map.py) 打开已有映射，不创建新映射、不缩小生产共享区。帧耗时工具需要已有有效样本，否则给出错误提示。F8 诊断仅在相应模块存在并写入数据时有意义，默认禁用版不能据此显示“已正常开启动作”。

## 本次公开整理的实测记录（2026-11-04）

- 原公开副本：relay 与插件构建通过；默认 addon MSVC 构建通过；MC 64 个 Java 文件配本机依赖直接编译通过。
- 这次修改的 BridgeControl：.NET 8.0.426 构建通过，0 警告/0 错误。首次使用 .NET 10 的包源还原被本机代理/沙箱连接限制中断，改用已安装的 .NET 8 及本地依赖完成构建，未修改协议来绕开问题。
- 新独立 GPU 队列测试：80 项通过，界限检查通过；本次最长调用约 0.204 ms，仅记录本机 WARP 样本。
- MapCutRender：.NET 10 构建 0 警告/0 错误；临时新工作区 dry-run 报告落在新根，缺失/相对根路径拒绝，build-cache 以外及游戏目录输出拒绝；未使用游戏资源。
- 7 个 PowerShell 脚本通过语法解析，9 个 Python 源文件通过 AST 解析。
- **未运行这 7 个 live 场景，没有为发布重开游戏或修改存档。** Gradle 干净打包、F8 实机黑影/附件/回放镜头验收仍未完成。

复刻者应另建自己的记录，不能把上面这次结果当作新机器通过。

## 推荐记录格式

```text
commit:
stage:
OS / GPU / driver:
game / map / dependency versions:
command (redact token):
inputs and their SHA256:
exit code:
expected:
observed:
log / screenshot paths:
PASS / FAIL / NOT RUN:
next smallest check:
```

日志保留在本地；分享前移除令牌、账户和无关进程/路径信息。截图按测试项命名，能看到动作之前、之后与必要状态即可。
