# MC × CS2 Bridge

把 Minecraft 的方块、交互与画面合成到 Counter-Strike 2 本地离线场景中的实验性项目。

项目由三个运行组件组成：Minecraft Fabric 模组、CS2 CounterStrikeSharp 插件和 ReShade 原生合成器；一个本机 relay 负责转发控制消息。Minecraft 处理方块世界、移动与互动，CS2 保留地图、干员和武器画面，合成器根据深度关系组合两边的图像。

此仓库公开源码和制作思路，不提供游戏本体、地图 VPK、提取的模型与纹理、比赛 Demo、账号信息或私人运行配置。运行时需要自行准备对应游戏和依赖。历史开发环境使用本地离线 CS2；源码包含 `-insecure` 启动检查；复现时使用 `-insecure +sv_lan 1`，不适用于匹配对局或受反作弊保护的在线客户端。

## 可以研究什么

- 两款游戏之间的位置、视角、输入与事件同步。
- Minecraft 场景、HUD 与 CS2 画面的深度合成。
- 方块放置、碰撞同步、伤害与生物互动。
- 成年干员互动、小 T 生成与追击等实验玩法。
- 地图离线预切与运行时方块覆盖组成的局部地形破坏实验。
- F8 本地回放动作：对识别到的原始人物及附件实施压缩、拉伸和旋转显示变换。

## 具体实现与换电脑复刻

文档按实际函数和消息链说明每个功能怎么完成，也列出未闭合的流程；不是只提供设计思路。

| 想看什么 | 文档 |
|---|---|
| 另一台 Windows 电脑逐步重建 | [新机手册：版本、配置、命令、检查点与故障定位](docs/reproduce.md) |
| 干员互动、金苹果、小 T、近战、伤害、语音、水印 | [玩法具体实现](docs/features-gameplay.md) |
| 画面合成、相机、F8、手套/C4 附件、黑影与性能 | [渲染具体实现](docs/features-rendering.md) |
| 地图碰撞、切坑、物理与渲染、C4 地形联动 | [地形具体实现](docs/features-terrain.md) |
| 哪些验证能直接运行，哪些会改世界 | [测试工具与结果说明](docs/testing.md) |
| 让下一次 Codex 少重复排查、减少 token | [Codex 执行与接力清单](docs/codex-handoff.md) |
| 这次 Codex 实际整理、改动、验证了什么 | [公开整理执行记录](docs/publication-worklog.md) |
| 整体数据流与组件构建 | [架构](docs/architecture.md) · [构建](docs/build.md) |

源码入口：[CS2 插件](source/cs2-bridge/plugin/)、[MC 模组](source/universal-modder/examples/minecraft-gta5-passthrough/mc/)、[原生渲染](source/cs2-bridge/native/)、[FX](source/cs2-bridge/shaders/)、[工具](tools/)。

当前尚无干净新机端到端验证，不能承诺百分百复刻。手册把缺失的输入、依赖与待验证项列在对应步骤，避免重复摸索。

## 状态与限制

这是开发中的源码快照，不是下载即用的整合包。部分路径、模型识别数据及着色器变体与开发环境绑定，新的游戏版本需要重新验证。

公开版默认禁用依赖本地提取数据的 F8 动作模块，保留其源码和设计供研究。开发版已有离线 GPU 输出测试和部分游戏内观察记录，但警方黑影、附件跟随及第三人称鼠标绕人物拍摄的完整验收尚未结束。不能据此保证所有地图、干员、手套或回放都正常。动作渲染也存在明显性能开销。

地形破坏只发生在预先处理的区域，不能动态挖穿任意 CS2 地图。小 T 追击、碰撞、镜头和跨进程恢复仍有待改进。

维护与 CS2 扩展：**PhantomXrui / Bilibili:Phantom_rui**。

代码继承自 [universal-modder](https://github.com/rehan-remade/universal-modder) 的 Minecraft passthrough 示例，并扩展为 CS2 联动。保留仓库内的 MIT 许可和上游版权声明；第三方组件遵循各自许可证。
