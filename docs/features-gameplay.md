# 玩法功能：从触发入口到实际代码

本文记录公开源码中的实际调用链，便于继续开发、定位问题和复现。参数是当前实现值；“存在实现”不等于已经在所有地图、游戏版本和回放环境验收通过。这里的角色互动指游戏内的靠近、爱心粒子、镜头切换和生成小型机器人流程。

这些玩法主要依赖离线本地对局中的 CounterStrikeSharp 服务端插件、Minecraft 集成服务器和两端桥接。Demo 播放中能显示 F8 效果，不代表下面的服务端机器人创建、伤害、喂食操作也能执行。

## 1. 两端如何认出同一个干员

源码入口：[MC `MobWar.java`][mc-mob]、[MC `ActorInteractionMixin.java`][mc-interaction-mixin]、[MC `ActorInteractions.java`][mc-interaction]、[CS `ActorInteractions.cs`][cs-interaction]、[MC `HostLink.java`][host-link]。

1. CS 将人物快照通过 `peds` 消息发送给 MC。`HostLink.onMessage()` 解包后交给 `MobWar.peds()`；当前 10 列格式包含句柄、位置、血量、生命标识、落地状态、队伍、缩放和幼年标记。
2. `MobWar.syncProxies()` 为人物创建不可见、无 AI、无重力的村民代理，并维护 `handleOf`、`proxyLives`、`proxyTeams`、`proxyJuveniles`。玩家看到的是 CS 人物；MC 代理负责接收物品互动、碰撞和伤害。
3. MC 的 `ActorInteractionMixin` 调用 `ActorInteractions.interact()`。`eligible()` 要求桥接激活、`passthrough.vanillaCombat` 开启，且目标是仍存活的有效代理。服务端再检查玩家和互动距离，并以同目标 4 tick 间隔防止重复触发。
4. 消息中的 `h` 是人物索引，`life` 区分同一索引的不同生命，`seq` 用于去重。CS 的 `FindActor()` 还检查机器人、存活、队伍和生命标识，排除代替 MC 怪物的专用机器人。
5. MC 未受控时跟随 CS 快照；被牵引、乘船、成对互动时由 `ActorInteractions.controls()` 阻止快照覆盖位置。CS 侧的受控状态由 `ActorHold` 保存，超过 500 ms 没有更新或人物重生后，`TickActorInteractions()` 恢复原移动/AI 状态。

常用坐标比例为 **1 个 MC 方块 = 40 个 CS 单位**。常见转换为 `CS=(MC.x*40, -MC.z*40, (MC.y-YOffset)*40)`。不要在新增路径中自行猜测高度偏移；不同现有函数对偏移的处理并不完全一致。

## 2. 末地烛与玩家、T 干员的互动

实际实现位于 [MC `ActorInteractions.java`][mc-interaction] 的 `interact()`、`startRitual()`、`tickRitual()`、`endRitual()`。

**触发入口：** 持末地烛右键 T 队代理。当前流程只允许一个 `ritualHandle`，同时保存玩家 UUID、开始 tick。

**执行过程：**

- `startRitual()` 发送 `director` 的 `camera` 操作，模式设为 `1`，并调用 `Voice.play(Voice.DEPLOY)`。
- `tickRitual()` 计算玩家视线方向，将 MC 代理置于玩家位置后方约 `0.6` 格，再叠加 `sin(elapsed * 0.35) * 0.3` 的前后位移；朝向玩家，每 5 tick 发爱心粒子。
- 持续 60 tick，按正常 20 TPS 约 3 秒。玩家/代理失效、玩家死亡或超过 80 tick 会退出。
- `endRitual(true, player)` 发送 `spawnjuvenile`，携带玩家 MC 坐标和递增时间来源的 `seq`；结束时把镜头设为模式 `0`。

**当前限制：**

- 末地烛分支只检查 T 队，没有调用用于排除小 T 的 `canBreed()`；因此“仅限成年 T”目前不是这条分支完整执行的条件。
- 这条动画只直接调用 MC 代理的 `setPos()`，没有像成对互动那样发送 `actorcontrol`，也没有设置 `State.controlled`。不能据此声称 CS 人物已经可靠地跟着完成位移动作；后续 `peds` 快照还可能覆盖代理位置。
- 镜头结束时直接设为模式 `0`，没有保存并恢复用户进入互动前的镜头模式。
- 生成请求走下文的独立 `spawnjuvenile` 路径，不携带父母句柄，也没有普通金苹果路径的成对校验。

## 3. 普通金苹果：两名成年 T 成对生成一个小 T

源码：[MC `ActorInteractions.java`][mc-interaction] 的 `feed()`、`findMate()`、`courtship()`、`requestBirth()`、`receiveBreedAck()`；[CS `ActorInteractions.cs`][cs-interaction] 的 `ApplyActorControl()`、`ApplyActorBreed()`、`TickActorInteractions()`。

### MC 输入和靠近阶段

`canBreed()` 要求成年、有效、存活的 T 代理。默认食物是 `minecraft:golden_apple`；`reloadConfig()` 可从 `config/actor-interactions.json` 的 `feedItems` 读取普通食物列表，每 5 秒检查更新。配置只接受有效物品，明确将附魔金苹果留给特殊分支；读取失败保留上一次可用值。

`feed()` 消耗一个物品，设置 `loveUntil=now+600`，也就是正常 TPS 下 30 秒的配对窗口。冷却中、已经喂过或正在等待出生结果时拒绝重复喂食。

`findMate()` 在 8 格内选择最近的另一名已喂食成年 T，建立双方 `mate`/`mateLife` 关系；解除绳索和乘坐，双方进入受控状态。`courtship()` 只由句柄较小的一方推进配对，避免双方重复计时：

- 超过 12 格、配对关系/生命失效、等待窗口过期则解除。
- 每 tick 最多靠近 `0.06` 格，接近到 `0.75` 格停止推进。
- `collidePlacedBlocks()` 收集 MC 放置方块的碰撞形状，按 Y、X、Z 轴裁剪位移，排除作为 CS 地形替身的支撑块。
- 每 tick 对双方发送 `actorcontrol`，`mode="courtship"`，包含 `h/life/seq/p/yaw`；每 10 tick 发爱心。
- 总持续至少 60 tick，且距离不超过 `0.85` 格连续至少 40 tick，才调用 `requestBirth()`。

### CS 接管和出生阶段

`ApplyActorControl()` 验证模式、有限坐标、生命、队伍、序号、速度和墙体射线。`MoveControlledActor()` 暂停机器人的原生 AI/移动并更新位置，同时延后武器攻击 tick；释放时恢复先前状态。这里的 MC 方块碰撞和 CS 地图射线是两层独立检查。

双方均未使用附魔金苹果时，MC 发送：

```json
{"t":"actorbreed","h":12,"life":101,"mate":13,"mateLife":102,"seq":1}
```

以上值只是字段示例。`ApplyActorBreed()` 在 CS 再次验证：双方是不同的成年 T、都处于 `courtship` 且至少已维持 2500 ms、没有 60 秒出生冷却、彼此距离不超过 `0.95` 格、本地玩家距其中一方不超过 12 格、T 机器人少于 20、总人数未达到 `min(24, Server.MaxPlayers)`。

通过后记录现存 controller 的完整句柄集合，将 `bot_quota` 仅增加一个名额，并保存 4 秒截止时间。`TickActorInteractions()` 识别新出现的 T，调用 `RegisterChild()`、移到父母中间附近；`FinishBirthPopulation()` 移除本次多出来的机器人，只保留选定小 T，并恢复正确配额。父母和小 T 都记录 60 秒冷却。

CS 返回 `actorbreedack`，包含相同 `seq` 和 `ok`，成功时还包含孩子标识；MC 的 `receiveBreedAck()` 按序号结束等待、释放父母、记录冷却并发出 `actorbreedresult`。拒绝原因包括 `busy`、`parents`、`juvenile`、`courtship_required`、`cooldown`、`distance`、`population_limit` 等。MC 等待超过 100 tick 也会释放。

这是有成对校验、完成回执和失败回收的路径；下面附魔金苹果的流程不同。

## 4. 附魔金苹果：四次排队请求与当前缺口

同样从成年 T 的 `feed()`、`findMate()`、`courtship()` 开始。只要配对任一方的 `enchanted` 标记为真，`requestBirth()` 就进入特殊分支：

1. 清除双方爱心、配对和等待状态，立即记录 1200 tick 冷却。
2. `scheduleMultiBirth()` 在父母中点排入四个时间点：当前 tick 后 `20 / 30 / 40 / 50` tick。
3. 播放“比特_T仔出动.wav”。`tick()` 到点逐次发送 `spawnjuvenile`；四个请求全部从队列移除后播放“比特_T仔永远是我的王牌.wav”。

这条分支**不发送 `actorbreed`**。CS 实际处理函数是 `ApplySpawnJuvenile()`：按 `seq` 去重，检查桥接状态、容量和 `soloBirth` 是否空闲，换算坐标，然后增加一个机器人名额；`TickActorInteractions()` 在 4 秒内等待新 T，缩小并放到请求位置，返回 `juvenileack`。末地烛结束后也使用这个入口。

当前代码有几个直接影响结果的边界：

- `soloBirth` 同时只能保存一个请求；忙碌或容量不足时直接返回，没有拒绝回执。因此 0.5 秒间隔的四个请求**不保证生成四个孩子**。
- [HostLink 的消息分发][host-link] 有 `actorbreedack`、`actorcontrolack`，没有为 `juvenileack` 建立出生状态处理；不能把插件发出了该消息等同于 MC 已收到并确认成功。
- 收尾语音取决于 MC 队列发送完毕，不取决于孩子实际出生成功数量。
- 独立生成接口不验证父母、成对互动时长或父母冷却；它不能替代普通 `actorbreed` 的完整条件链。

后续改进应围绕“每笔请求明确成功/失败回执 → MC 按回执推进下一笔 → 按实际成功数播放反馈”，而不是只增加发送间隔。这是从现有代码得出的待改进方向，本文没有修改玩法实现。

## 5. 小 T 的尺寸、近战与语音

源码：[CS `ActorInteractions.cs`][cs-interaction]、[CS `JuvenileAi.cs`][cs-juvenile]、[MC `Voice.java`][mc-voice]、[CS `Voice.cs`][cs-voice]。

### 小型人物如何实现

`RegisterChild()` 将 controller/pawn 完整句柄与原始尺寸保存在 `ChildActor`。`ApplyChildScale()` 使用 `ChildScale=0.5`，同时调整场景节点的 scale/absolute scale/client scale、碰撞边界、包围半径、胶囊半径和视点高度，不只是视觉缩小。

`TickChildren()` 验证人物身份；重生得到新 pawn 后重新取尺寸基线，避免重复乘半。身份不符、离开 T 队等情况由 `RestoreChild()` 尝试恢复原值。MC 通过 `peds` 得到幼年标记，在 `syncProxies()` 中把代理设为幼年并维持年龄、尺寸和血量对应；这不是 MC 村民自然长大的一套年龄过程。

### 小 T 怎样选择和攻击目标

`TickJuvenileAi()` 每 100 ms 执行一次，只选择活着的幼年 T 机器人。先计算所有孩子的群体中心，再选择距中心最近的目标，全体共用该目标。

目标收集先加入所有存活 CT，**没有排除真人 CT**，再加入 `mobProxies`。接近时每步沿目标方向最多推进 20 个 CS 单位，额外使用与行进方向垂直的 `28` 单位队形偏移，直接 `Teleport()`。距离小于 70 CS 单位时调用 `TryJuvenileHit()`。

- 冷却表 `juvenileHitAt` 以**目标索引**为键，因此同一目标每秒最多结算一次，所有孩子共享，不是每名孩子每秒各打一次。
- 普通 CT 分支直接将血量减 1，并以 1 HP 为下限；不会通过这条分支击杀 CT。
- 标记为怪物代理的分支发送 `mobdmg`，伤害 `d=4`，由 MC 结算。
- CT 命中播放“比特_T仔抓到他们了.wav”，降到 1 HP 时再播放“比特_干得漂亮T仔.wav”。

**现有限制：** 这段是位置推进与近距离扣血，没有导航网格寻路、墙体/地面射线、真正的咬合接触或独立近战动作。队形偏移是每次直接加到下一位置，复杂地形和多孩子情况下需要实测。怪物代理自身通常也是 CT；目标表没有去重，同一实体可能先以普通 CT 再以怪物出现，距离排序可能优先选中普通 CT 项，不能保证上述 `mobdmg` 分支在实际场景中按意图触发。

### 语音怎样接入

MC `Voice.play()` 通过 Java Sound 读 WAV，按文件名管理 `Clip`，重复同名播放会先停止旧片段；目录可由 `passthrough.voiceDir` 指定，默认相对项目的 `docs/voice-lines/audio`。CS `PlayVoice()` 用 Windows `winmm.dll` 的异步 `PlaySound()` 读取同目录文件。

公开的是播放逻辑和文件名约定；外部录音、生成音频等素材需要自行准备。缺少文件会记录日志并跳过，源码中的语音调用不意味着仓库附带声音资产；两端也没有一个统一的语音排队器。

## 6. 牵绳、上船与木棍开关

源码：[MC `ActorInteractions.java`][mc-interaction] 的 `interact()`、`tick()`、`board()`、`release()`，及 [CS `ApplyActorControl()`][cs-interaction]。

- 右键有效 T/CT 代理使用拴绳，MC 建立真实绳索关系并记录 `controlled`。玩家手持剪刀、空手释放，或潜行持绳互动时调用释放逻辑。已牵住后再次互动会尝试上附近船。
- 跟随通过 MC `tick()` 计算位置，距牵引者保留约 2.25 格，每 tick 最多移动 `0.35` 格，先裁剪放置方块碰撞，再发送 `actorcontrol` 的 `leash` 模式。超过 24 格或牵引者死亡会解除。
- `board()` 查找附近 2.25 格内的有效船，用原版 `startRiding()` 和船容量规则；随后发送 `boat` 模式。CS 在船模式中处理蹲姿与移动状态，退出后恢复之前的值。
- 木棍右键 T 发送 `director / dance_toggle`，进入 F8 对应的动作开关链。具体渲染实现应结合 `SquashDance` 及原生渲染代码阅读。

这些操作要求有效的 MC 代理与 CS 活跃实体；不能把它们理解成对任意回放人物都能执行的离线命令。

## 7. 补满 T 与多种探员外观

源码：[CS `UniqueTAgents.cs`][unique-t]、[`UniqueTAllocation.cs`][unique-allocation]、[`UniqueTAgentCatalog.cs`][unique-catalog]、[`TeamCapacity.cs`][team-capacity]。

入口命令为 `css_mccs_fill_t_unique [0-64 数量]`；`css_mccs_unique_t_status` 输出实际皮肤应用状态。`LocalUniqueTSession()` 要求支持的地图、只有一个已连接真人且其地址为本地/loopback。

`LoadUniqueTAgents()` 在资源预缓存阶段注册 `SimpleDanceAgents`；填充前 `StopDance()`，避免换模型时继续操作旧骨骼。`FillLocalTTeam()` 根据真实 T 出生点、规则容量、当前非 T 人数和 `Server.MaxPlayers` 计算名额，再设 `bot_quota`；不是输入 64 就保证生成 64 名。

`EnsureBreedingSpawnCapacity()` 只在已有 T 出生区附近探索候选点，检查落地、头顶空间、身体宽度和与已有点的距离，创建额外出生点，最多以 20 为繁殖容量目标。`StartDefaultTAgents()` 等待连接、地形和预缓存就绪后尝试设置 10 名 T，为新增小 T 留空间。它会重试等待条件，但不保证地图一定容得下 20 名。

外观分配由 `UniqueTAllocation.Reconcile()` 以完整 controller 句柄稳定分配整数编号，保留仍在线者编号，给新来者最低空闲编号。**实际选择是 `SimpleDanceAgents[index % SimpleDanceAgents.Length]`，当前池只有 7 款**，所以人数超过 7 会循环重复，不是每个人绝不重复；较大的 `UniqueTAgents` 目录不等于所有模型都实际启用。

`ApplyUniqueTAgents()` 调 `pawn.SetModel()`；幼年人物再调用 `ApplyChildScale()`。每个 controller/pawn 最多 3 次尝试；复活事件在 0.15 秒、0.65 秒重试，初次补人还进行有限轮次扫描。`ReportUniqueTAgents()` 比较模型实际值与预期值，写 `unique-t-agents.json`，可以据此区分“分配过编号”和“模型已真正应用”。

## 8. 血量、盾牌、金苹果效果与 MC 伤害

源码：[MC `HostVitals.java`][host-vitals]、[`PlayerVitalsMixin.java`][vitals-mixin]、[MC `MobWar.java`][mc-mob]、[CS `PlayerVitals.cs`][cs-vitals]、[`CombatFeedback.cs`][combat-feedback]、[`MinecraftBridge.cs`][cs-main]。

### 玩家生命值

CS `SendPlayerVitals()` 发 `vitals`，MC `HostVitals.receive()` 缓存；`beginTick()` 按 `CS health/max` 比例更新 MC 血条和吸收量，避免把回写当成新的治疗，也不会用正血量强行复活已经死亡的 MC 实体。快照超过 2 秒失效。

MC `PlayerVitalsMixin` 拦截 `heal()` 和吸收量变化，发送 `playerheal` / `playerabsorption`，带 `life/seq/amount`。CS `ApplyPlayerVitals()` 拒绝旧生命/旧序号/非有限或绝对值超过 40 的量，将 MC 治疗乘 5、保留小数余量，CS 血量上限 100。MC `endTick()` 另外发送抗性等级和抗火状态 `vitalsfx`，并按有效水平移动累计饥饿消耗。

CS `ProtectWithMCConsumables()` 只信任 1500 ms 内收到的效果状态。当前本地玩法规则是：**存在吸收缓冲时子弹和霰弹直接归零，且这一子弹分支不扣吸收缓冲**。其他伤害经过抗火、每级 20% 抗性和吸收量抵扣；不是原版金苹果受弹后逐点消耗金心的完全复刻。

### 盾牌与原生伤害

`BeforeNativeDamage()` 钩住 CS 原生伤害，确认桥接状态后处理特殊保护。盾牌只在举盾消息仍新鲜、输入状态符合条件、攻击来源位于玩家前方，且伤害属于子弹/霰弹/劈砍/钝击时拦截；命中反馈发 `shieldhit`。被控制的互动机器人和安静互动场景机器人还有独立的保护玩家逻辑。

MC 原版伤害先作用于不可见代理，`MobWar.onProxyDamage()` 发 `actorhit`，带实际伤害、来源、击中点、攻击类型、人物生命和是否归属玩家。CS 分发器校验生命与序号，通常按 MC 伤害乘 5；但当前代码对 `player=true` 的有效伤害启用本地练习的一击击杀，将伤害至少提高到目标现有血量。

`HurtNative()` 创建短生命周期 `point_hurt`，进入原生伤害流程；`BeforeNativeDamage()` 修正攻击者和伤害归属，`EmitNativeBlood()` 只在血量确实减少时生成血粒子。粒子上限 32、约 2 秒清理。`explosion` 通知不再次按半径扣血，避免 MC 已计算遮挡和爆炸伤害之后重复结算。

## 9. MC 怪物、末影龙与 CS 火力

源码：[MC `MobWar.java`][mc-mob]、[MC `HostLink.java`][host-link]、[CS `MinecraftBridge.cs`][cs-main]、[CS `ActorInteractions.cs`][cs-interaction]。

入口包括正常 MC 生物生成和 `spawnmobs` 消息。`HostLink` 将类型、数量、半径、扇形角度及可选固定地点交给 `MobWar.spawn()`。`MobWar.onEntityLoad()` 为可战斗生物添加 `ProxyTargetGoal`、扩大追踪范围；在原版战斗模式下 `isEnemyProxy()` 选择 T 代理，一些依赖 Brain 的怪物由 `tick()` 每 10 tick 补充 `ATTACK_TARGET`。

`reportMobs()` 每 tick 报告玩家附近 96 格范围内战斗生物的位置、类型和瞄准点，末影龙用头部中心。CS `UpdateMobProxies()` 为普通怪物创建最多 4 名专用 CT 机器人，跟随怪物、隐藏渲染、移除武器并抑制开火；它们让原生 CS AI 获得可识别目标，不直接代表 MC 实体的生命值。

CS 枪击经代理受伤事件或子弹线段与怪物瞄准点接近检测，发送 `mobdmg`。MC `MobWar.damage()` 将请求入队，`applyDamage()` 在服务器线程调用原版伤害；末影龙通过头部 multipart 和特殊爆炸伤害源结算，这个伤害源本身不创建破坏方块的爆炸。

Boss 弹幕目标走另一条路径：`TickWitherBarrage()` 让未受控的成年 T 选择射程内且可见的 Boss 目标，给枪、设置瞄准和射击标记并补弹；幼年 T 不进入该成年弹幕逻辑。末影龙 `directDragon()` 选择 T 代理，设置 `STRAFE_PLAYER` 阶段和飞行攻击目标，目标不存在则悬停。

限制是代理槽位、地图遮挡和 MC 各生物自己的 AI 都会影响表现；上述小 T 的怪物目标重复问题也仍需单独解决。源码包含事件和诊断输出，不应只看枪口闪光就判定双向伤害成功。

## 10. 末影珍珠、传送与下界扩散

源码：[MC `ServerPlayerMixin.java`][teleport-mixin]、[CS `MinecraftBridge.cs`][cs-main]、[MC `Nether.java`][mc-nether]、[`PortalMixin.java`][portal-mixin]。

末影珍珠和 `/tp` 使用通用传送链：`ServerPlayerMixin.passthrough$teleported()` 在 MC 传送入口读取目标位置；桥接激活且 MC 不掌管移动时发送 `pteleport`，CS 转换坐标并 `pawn.Teleport()`。如果启用 `MinecraftMovementActive`，CS 忽略这条重复消息，由 MC 权威移动状态携带传送。因此不是给末影珍珠另写一套飞行物理。

下界代码保留的是“下界从传送门向当前世界扩散”的效果。`Nether.tick()` 检查玩家脚部/头部是否位于下界门，发送 `inportal`；第一次进入时 `start()` 搜索相连门块，`planSpread()` 规划半径 26 格的区域与岩浆路径，`spread()` 在 200 tick 的时间尺度内推进。`waves()` 前 5 分钟分批生成猪灵系、恶魂、凋灵骷髅、烈焰人、岩浆怪，生成器还限制附近战斗生物数量。

`set()` 保存改动前方块，`stop()`/`close()` 用于结束和恢复；`onBlockChanged()`、`flushHot()` 记录火与岩浆变化。`PortalMixin` 在服务端取消正常进门传送，避免直接换到另一个没有 CS 地图对应关系的维度。

注意 `Nether` 是保留在 MC 模组中的实现。当前 CS 插件消息分发没有对应的 `nether` / `inportal` 专用处理，不能照搬其他宿主的说明，宣称 CS 端也已经实现了夜色、传送门镜头扭曲或完整环境切换。

## 11. ID 水印的两种实现

源码：[MC `WatermarkHud.java`][watermark-hud]、[`PassthroughClient.java`][mc-client]、[MC `ActorInteractions.java`][mc-interaction]、[CS `SquashDance.cs`][cs-dance]。

**固定 HUD 字样：** `PassthroughClient` 注册 `WatermarkHud`，`extractRenderState()` 用 MC 字体画 `Bilibili:` 和 `Phantom_rui`，前缀粉色、名字白色、有阴影。当前实际坐标为横向居中、`guiHeight()-52`，位于血条/饥饿条上方；文件顶部“右下角”的旧注释与实际坐标不同。它进入 HUD overlay，而不是场景中一个物体。

**场景内文字实体：** `GiveWatermarkItem()` 给一个带指定名字的命名牌；`ActorInteractions.useWatermark()` 检查名称，普通右键发 `director / watermark`，潜行右键发 `watermark_follow`。

CS `PlaceWatermark()` 用当前镜头原点/方向求前方 2.5 格位置，再发 `cmd`，在 MC `summon minecraft:text_display`。跟随模式由 `ToggleFollowWatermark()` 先清旧实体再创建，`UpdateFollowWatermark()` 将带指定 tag 的实体移到镜头前 1.8 格、下偏 0.12 格。`ClearWatermark()` 按 tag 清理；不会删除所有 `text_display`。

场景文字和固定 HUD 的深度/合成行为不同。导出为剪辑水印图片是独立资产制作过程，不能把 `text_display` 的功能说明当作仓库已附带透明 PNG。

## 12. 独立的梗角色繁殖示例

[MC `MemeBreeding.java`][meme-breeding] 是另一套逻辑，入口由 `Passthrough.onInitialize()` 注册。它只响应带 `mccs_meme_carrier` 标签的成年牛，物品是生鸡肉 `Items.CHICKEN`；普通牛与 CS 干员不进入这套流程。

`initialize()` 注册 `UseEntityCallback`，喂食后 600 tick 内配对；`tick()` 在 8 格内找另一只已喂食载体，保存/暂停原 AI。`approach()` 使用精确宿主地形射线检查落点、侧向墙体与头部空间，再结合 MC 方块碰撞裁剪，每 tick 最多推进 `0.055` 格。

持续至少 60 tick 且靠近至少 40 tick 后，在两者侧边找落地点，父母设置 1200 tick 冷却，再排队执行 `execute ... summon minecraft:cow run function mccs_meme:child`。孩子的最终外观依赖 `mccs_meme:child` 数据包函数和配套素材，不能仅靠 Java 文件保证完整复现；此路径不创建 CS 小 T，也不发送 `actorbreed`。

## 13. 建议如何验收这些链路

本次文档是源码核对，没有运行游戏验收。复现时应同时检查触发、消息和实体结果：

| 要验证的功能 | 应观察的证据 | 不能代替成功证据的现象 |
| --- | --- | --- |
| 普通金苹果配对 | 双方 `courtship`、对应 `actorbreedack` 成功、一个新 T、缩放和冷却 | 只出现爱心 |
| 附魔金苹果四个小 T | 每笔 `spawnjuvenile` 对应的实际新增实体和结果 | 四条发送日志、收尾语音 |
| 末地烛动作 | CS 人物与 MC 代理位置一致，镜头进入/退出正确 | 仅 MC 代理移动 |
| 小 T 近战 | 目标类型、是否穿墙、每秒血量变化、CT 的 1 HP 下限 | 小 T 已靠近 |
| 外观填充 | `unique-t-agents.json` 中 `actual` 等于 `expected`、复活后保持 | 分配了编号但实际模型没换 |
| 双向伤害 | MC `mobdamage` 与 CS 伤害诊断的前后血量、归属、生命标识 | 粒子、枪声或动画 |

这些可观察点也是继续改进时应保留的诊断接口。当前的排队回执、末地烛实际宿主运动、幼年 AI 寻路和目标去重问题，应分别修复并在真实本地对局中验证。

### 13.1 在另一台电脑先确认配置与状态

完成主 README 的构建和本地启动步骤后，从仓库根目录操作。需要实际离线对局、MC 桥接已激活、本地玩家存活；不能只打开 CS 主菜单。MC 启动 JVM 参数须包含 `-Dpassthrough.vanillaCombat=true`，否则 `eligible()` 不接受这些互动。普通食物配置已经随仓库提供在 [`config/actor-interactions.json`](../config/actor-interactions.json)：

```json
{"feedItems":["minecraft:golden_apple"]}
```

可以改普通食物；保留附魔金苹果专用逻辑时，不要通过配置把它混入普通列表。配置支持热读，正常等待 5 秒后再查状态。指定外置文件时使用 `-Dpassthrough.actorInteractionsConfig=你的配置绝对路径`；语音可用 `-Dpassthrough.voiceDir=你的WAV目录绝对路径`。这些是 Java 系统属性，必须给 MC Java 进程，而不是输入 CS 控制台。

构建并运行现有 [BridgeControl 工具](../source/cs2-bridge/tools/BridgeControl/Program.cs)，一次取回 MC 代理状态与 CS 状态，避免为了查一个句柄反复读整个项目：

```powershell
$env:MCCS_ROOT = (Get-Location).Path
dotnet build .\source\cs2-bridge\tools\BridgeControl\BridgeControl.csproj
New-Item -ItemType Directory -Force -Path .\temp, .\logs | Out-Null
$commands = @(
    @{t='actorinteract';op='inspect'},
    @{t='director';host='cs2';op='actor_controls'},
    @{t='director';host='cs2';op='inspect'},
    @{wait_ms=500}
)
$json = ConvertTo-Json -InputObject $commands -Depth 8
[IO.File]::WriteAllText((Join-Path $env:MCCS_ROOT 'temp\gameplay-inspect.json'), $json, [Text.UTF8Encoding]::new($false))
dotnet .\source\cs2-bridge\tools\BridgeControl\bin\Debug\net8.0\BridgeControl.dll .\temp\gameplay-inspect.json .\logs\gameplay-responses.json
```

工具连接本机 `127.0.0.1:25599`，从本地 `runtime/bridge-token.txt` 读取启动时准备的令牌，不需要把令牌写进 JSON、文档或公开仓库。输出分工如下：

| 文件 / 响应 | 具体检查字段 |
| --- | --- |
| `logs/gameplay-responses.json` 中 `actorinteractionstate` | `feedItems`、`lastAction`、`player.held/range/p`、`actors[].h/life/team/juvenile/scale/mate/courtTicks/pending` |
| `logs/actor-controls.json` | CS `holds` 的 `mode` 与位置、`pendingBirth`、`children` |
| `logs/cs2-live-state.json` | 地图、`connected/mcReady`、玩家/机器人队伍、血量与 CS 坐标 |
| `logs/unique-t-agents.json` | 通过 `css_mccs_unique_t_status` 生成，检查 `applied` 和每人的实际模型 |

`BridgeControl` 的响应过滤列表也没有 `juvenileack`；当前工具不能凭这个响应文件完整统计附魔金苹果的生成成功率，需要对照新增人物和孩子状态。这是工具现状，不应把空白回执当成成功。

### 13.2 最小手动复现顺序

1. 在支持的离线地图用 `css_mccs_fill_t_unique 10` 准备 T；待模型生成结束，查看状态确认至少两名成年 T，记录它们的 `h` 和 `life`。
2. 普通金苹果测试：先右键喂一名，再喂距它 8 格内的第二名；玩家保持在附近。先检查 `loveTicks`，再检查双方 `mate/courtTicks/controlled`，最后确认 `actorbreedack` 和只增加一个幼年 T。
3. 附魔金苹果测试使用另一对无冷却的成年 T，至少一方吃附魔金苹果，另一方也要完成有效喂食。观察四次发送与实际新增人数是否一致，单独记录失败或丢失情况。
4. 末地烛单独测试，记录开始/结束镜头模式，同时比较 MC 代理坐标与 CS 人物坐标，避免把生成孩子掩盖了动作未同步的问题。
5. 小 T 攻击先在空地测试一名 CT，再测试隔墙和高低差；每次检查目标是否真人/机器人/MC 代理，以及实际血量是否按预期变化。

也可通过 `{"t":"actorinteract","op":"interact","h":实际句柄}` 复用真实右键分支，但玩家必须已经拿着相应物品并处于交互范围内；它不是越过物品、距离、队伍校验的强制命令。`op="feed"` 只接受配置中的普通食物；测附魔金苹果、末地烛应使用 `interact` 或真实右键。`h` 必须来自当前状态，不能照抄本文示例的编号。

### 13.3 现有自动脚本的覆盖范围

仓库提供以下**会改动当前测试世界**的脚本。它们使用固定场景坐标；先阅读 [测试说明](testing.md)，在测试存档和对应地图运行，不要将它们当成任意地图上的通用自检。

| 脚本入口 | 实际覆盖 | 结果文件 |
| --- | --- | --- |
| [`tools/test-bridge-interactions.ps1`](../tools/test-bridge-interactions.ps1) | 固定墙体的阻挡/开洞，以及放置/破坏方块；不是金苹果繁殖测试 | `logs/interaction-test-result.json` |
| [`tools/test-bridge-damage.ps1`](../tools/test-bridge-damage.ps1) | 在目标机器人附近生成 TNT，再比较血量 | `logs/damage-test-result.json` |
| [`tools/test-vanilla-combat.ps1`](../tools/test-vanilla-combat.ps1) | 弓、弩、三叉戟、药水、状态、火、TNT 等；`-Extended` 和 `-EdgeCases` 切换用例组 | `logs/combat-v023-*.json` |

例如，选定场景准备好以后，运行 `powershell -NoProfile -File .\tools\test-vanilla-combat.ps1 -Only bow_full_draw` 只检查满蓄力弓用例，避免一口气执行无关操作。扩展用例应同时指定对应组，例如 `-Extended -Only diamond_sword`；不存在的用例名不会自动帮你切换组。

### 13.4 给下一次代码修改的最短阅读范围

| 要改的现象 | 先读这些函数 | 改后最少核对 |
| --- | --- | --- |
| 末地烛人物不跟着动 | MC `startRitual/tickRitual/endRitual`、`sendPose`；CS `ApplyActorControl` | 生命周期清理、CS/MC 位置一致、原镜头恢复 |
| 附魔苹果少生孩子/语音提前 | MC `requestBirth/scheduleMultiBirth/tick`；CS `ApplySpawnJuvenile/TickActorInteractions`；`HostLink.onMessage` | 每次请求有结果、忙时重试/排队、按实际成功反馈 |
| 小 T 穿墙或伤害对象不对 | CS `TickJuvenileAi/TryJuvenileHit` | 墙体/地面检测、目标去重、共享冷却与血量规则 |
| 换皮后小 T 尺寸异常 | `ApplyUniqueTAgents/RegisterChild/ApplyChildScale/TickChildren` | 模型切换和重生后尺寸不重复乘半 |
| 玩家/机器人伤害不一致 | MC `HostVitals/onProxyDamage`；CS `ApplyPlayerVitals/BeforeNativeDamage/HurtNative` | 生命标识、去重、倍率、一击规则和吸收规则 |

把“期望行为、当前状态 JSON、对应函数、一个最小复现场景”交给下一位维护者即可。无需每次重新扫描游戏资产、下载 Demo 或读取全部历史记录。

[mc-mob]: ../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/MobWar.java
[mc-interaction]: ../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/ActorInteractions.java
[mc-interaction-mixin]: ../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/mixin/ActorInteractionMixin.java
[host-link]: ../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/client/java/dev/rehan/passthrough/client/HostLink.java
[cs-interaction]: ../source/cs2-bridge/plugin/ActorInteractions.cs
[cs-juvenile]: ../source/cs2-bridge/plugin/JuvenileAi.cs
[mc-voice]: ../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/Voice.java
[cs-voice]: ../source/cs2-bridge/plugin/Voice.cs
[unique-t]: ../source/cs2-bridge/plugin/UniqueTAgents.cs
[unique-allocation]: ../source/cs2-bridge/plugin/UniqueTAllocation.cs
[unique-catalog]: ../source/cs2-bridge/plugin/UniqueTAgentCatalog.cs
[team-capacity]: ../source/cs2-bridge/plugin/TeamCapacity.cs
[host-vitals]: ../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/HostVitals.java
[vitals-mixin]: ../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/mixin/PlayerVitalsMixin.java
[cs-vitals]: ../source/cs2-bridge/plugin/PlayerVitals.cs
[combat-feedback]: ../source/cs2-bridge/plugin/CombatFeedback.cs
[cs-main]: ../source/cs2-bridge/plugin/MinecraftBridge.cs
[teleport-mixin]: ../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/mixin/ServerPlayerMixin.java
[mc-nether]: ../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/Nether.java
[portal-mixin]: ../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/mixin/PortalMixin.java
[watermark-hud]: ../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/client/java/dev/rehan/passthrough/client/WatermarkHud.java
[mc-client]: ../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/client/java/dev/rehan/passthrough/client/PassthroughClient.java
[cs-dance]: ../source/cs2-bridge/plugin/SquashDance.cs
[meme-breeding]: ../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/MemeBreeding.java
