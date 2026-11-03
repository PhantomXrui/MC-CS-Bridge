# 给下一台电脑的 Codex：少重复探索的执行清单

这是使用者可复制给编码助手的工作说明，不会自动授权游戏安装、删除文件或账号操作。功能设计和源码已保留，重点是重用已经确认的入口与检查点。

## 可直接复制的任务文本

```text
请协助在这台 Windows x64 电脑复刻 MC-CS-Bridge。
先读 README.md、docs/reproduce.md、docs/testing.md，以及本机 logs/reproduction-notes.md（若存在）。
以 git rev-parse HEAD 固定源码版本，建立当前机器的工具链和输入清单。
先完成 Dust2 + yOffset=96 的基础桥接；每阶段记录命令、退出码、输出文件、验收结果。
若外部输入缺失，明确报出缺什么和使用它的代码位置，继续完成不依赖它的阶段。
不把默认禁用的 F8 说成可用，不把 javac 通过说成 Gradle 打包通过。
先定位一个失败检查点，再修改对应模块；不全盘重写，不自动升级全部依赖。
保留 MIT 和上游署名，游戏资源、令牌、账号与存档不提交。
完成后写短记录，下一次只从未完成的检查点继续。
```

## 最省上下文的阅读路线

1. 读 README 的状态和阅读顺序，确认请求是构建、修某功能还是公开整理。
2. 读 `reproduce.md` 当前失败阶段；不要一开始把全部 Java/C#/C++ 读进上下文。
3. 在下表选对应文档，再读其中标出的关键函数及调用者。
4. 用 `rg -n` 搜索消息名、函数名或错误字符串，限定子目录；把独立查询合并执行。
5. 修改后运行有关检查。无新变化、新失败或未解风险时，不重复全套测试。

| 当前任务 | 优先阅读 | 最小定位词 |
|---|---|---|
| 连接不上 | reproduce.md 第 1/6/9 节；HostLink、relay、MinecraftBridge | `hello`、`MC-CS-Bridge-v1`、`25599`、`connected` |
| 成人互动/小 T | features-gameplay.md | `actorbreed`、`spawnjuvenile`、`soloBirth`、`juvenileack` |
| 血量/攻击 | features-gameplay.md | `mobdmg`、`actordmg`、`vitals`、`ActorLife` |
| 水印/音效 | features-gameplay.md | `WatermarkHud`、`Voice`、`voiceDir` |
| 放置/碰撞/重连 | features-rendering.md；features-terrain.md | `blocksync`、`BlockResync`、`HostTerrain` |
| 黑影/附件/F8 | features-rendering.md | `pawn_dance`、`cache`、`readback`、`ownership` |
| 地图切坑/C4 | features-terrain.md | `PhysicsMeshCutter`、`MapCutRunner`、`SpawnMinecraftTnt` |
| 第三人称拍摄 | features-rendering.md | `AvatarCamera`、`observer`、`camera`；先辨认实战/回放路径 |

例如，从根目录只查出生链：

```powershell
rg -n 'actorbreed|spawnjuvenile|soloBirth|juvenileack' source/cs2-bridge/plugin
rg -n 'actorbreed|spawnjuvenile|juvenileack' source/universal-modder/examples/minecraft-gta5-passthrough/mc/src
```

## 不能重复踩的坑

- **端口角色**：MC 是 WebSocket server；relay 等待 CS2 pipe 后再连接 MC。没有 connected 不一定是端口没开放。
- **两条动作路径**：插件游戏逻辑动作与 native 本地 Demo 的 F8 显示变换不同，先确定当前路径。
- **未完成闭环**：附魔金苹果多次请求与插件单笔出生队列有冲突，缺对应确认处理，不可按发包次数认定成功。
- **代理不是实体本体**：MC proxy 位置变化不等于 CS pawn 已被控制，要对照消息和 ack。
- **地图比例**：40 CS 单位 = 1 MC 方块；轴交换和 Y 反向不能漏；部分变换仍固定偏移 96。
- **F8 输入不足**：三个生成器不是完整从零流水线，武器生成器依赖已有基线，metadata/fingerprints 没有随公开包提供。
- **不能恢复阻塞渲染**：不要在 draw 回调中反射、编译 shader 或等待 GPU；现有读回队列有容量、字节和每帧上限。
- **改图双链**：物理和渲染必须一起改；只清 MC 覆盖层不能挖穿原地图。Inferno C4 分区不是通用规则。
- **测试名称可能误导**：`test-bridge-interactions.ps1` 测墙与放拆方块，不是完整繁殖测试。
- **路径不要猜**：公开副本采用 MCCS_ROOT；部分历史辅助工具仍有目录约定，先检查具体工具，不创建整盘映射来隐藏错误。

## 一次改动的记录模板

```text
目标：一个可观察的结果。
现状：提交、游戏/地图版本、PASS/FAIL/NOT RUN。
证据：必要日志字段，令牌不输出。
入口：文件与函数，不复制整文件。
修改：做了什么、影响哪条消息/数据流。
验证：命令、退出码、行为结果。
剩余：尚未验证的具体环节和下一条检查命令。
```

保存在忽略的 `logs/reproduction-notes.md`。长期稳定的接口结论才回写公开 docs，临时进程号、个人路径和完整日志留在本机。

## 减少 token 的实际做法

- 一次聚焦一个功能或阶段；涉及协议时同时检查两端，避免修完一端再发现字段不一致。
- 输出先写日志，给助手看错误附近、结尾和退出码；不反复贴几万行成功输出。
- 保存依赖和生成结果的版本/hash 清单，源码和输入未变时复用结果。
- 文档记录入口、算法、字段、参数、限制和验证，不复制整份源码。
- 独立模块可以并行检查；文件修改分工明确，避免互相覆盖。
- 交接给出当前阶段、已通过项、一条失败命令和日志位置，不必重述全部聊天。

这些做法减少重复分析，不承诺具体费用或 token 数，也不会自动补齐缺失依赖。
