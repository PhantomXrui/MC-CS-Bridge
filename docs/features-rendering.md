# 画面、输入、碰撞与 F8 动作的具体实现

本文按实际调用链解释这些功能如何做出来。源码中的部分变量仍叫 `GtaLightTex`、`host` 或 `minecraft-gta5-passthrough`，这是上游命名保留；本项目这一条接入路径的宿主是 CS2，深度输入是 `CSDEPTH`。

**公开版默认不编译原生 F8 动作。** [build-msvc.cmd](../source/cs2-bridge/native/build-msvc.cmd) 只有在 `MCCS_ENABLE_EXPERIMENTAL_DANCE=1`，且四个本地生成头文件齐全时才启用实验实现。默认使用 [pawn_dance_disabled.h](../source/cs2-bridge/native/pawn_dance_disabled.h)，按键会记录“本构建不可用”，不会假装已经开启。公开仓库包含算法与生成工具源码，不附本地生成识别表、着色器缓存、原始游戏模型、游戏着色器及地图资源。

下文描述的是源码已经实现的机制。**警方黑影、全部附件跟随和 Demo 第三人称鼠标绕人物拍摄的完整游戏内验收尚未结束**；已有校验不能推出所有模型、地图和回放都正常。

## 1. 两个游戏的画面怎样成为一张图

### 入口与数据

Minecraft 端的 [FrameExporter.java](../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/client/java/dev/rehan/passthrough/client/FrameExporter.java) 提供 `captureWorld`、`captureOverlay` 两个入口。世界绘制完、手部绘制前捕获世界颜色和深度，然后清空颜色目标；后续手部、屏幕效果、GUI 单独形成 overlay。这样 HUD 和手部不会误用三维场景遮挡规则。

三层通过 Windows 命名共享内存 `Local\MCPassthroughFrame` 传给原生插件。它不是截图文件轮询，也不是用 JSON 传像素。

| 位置 | 内容 |
| --- | --- |
| 头部 `0..4095` | magic `0x5450434D`、版本 1、槽数 3、槽跨度、最大尺寸 |
| `32`、`40` | 完整帧发布计数、最新槽下标 |
| `256 + i*128` | 第 i 个槽的描述符：序列号、MC 帧号、宿主帧号、尺寸、相机与时间 |
| 描述符 `+24/+28` | 此帧实际宽、高 |
| 描述符 `+32/+36/+40/+44` | near、far、垂直 FOV、深度/行方向标志 |
| 描述符 `+48..+80` | 位置 double x/y/z，角度 float yaw/pitch/roll |
| `4096 + i*stride` | 连续三层：RGBA8 世界颜色、float32 深度、RGBA8 overlay |

颜色使用预乘 alpha；每帧实际像素数据为 `width * height * 12` 字节。共享槽和 MC GPU 回读缓冲各有三个。生产者先把槽序列号改为奇数，写入数据后再发布偶数序列和最新槽，消费者只接受完整快照。

### 实际算法

1. MC 将 GPU 颜色、深度复制到可读缓冲；overlay 的异步复制完成回调再发布整帧。捕获时保存相机姿态，因此相机和像素属于同一时刻。缓冲忙时跳过本次捕获；`generation` 检查会拒绝过时回调。
2. [compositor.cpp](../source/cs2-bridge/native/compositor.cpp) 的 `upload` 检查发布计数、槽号、偶数序列、宽高及数据长度。它另有三个 D3D11 `FrameSet`，每组各含 world/depth/overlay 纹理和完成查询。
3. 只选择 GPU 已用完的备用组，使用 `GetData(...DONOTFLUSH)` 查询，再以 `Map(...DO_NOT_WAIT)` 写 staging 纹理，逐行遵守 `RowPitch`。无空闲组就继续显示上一张完整图，不等待 GPU。
4. CPU 拷贝结束后再读一次共享槽序列。发生变化就丢弃本次候选，避免颜色来自第 N 帧、深度却来自第 N+1 帧。
5. 成功后 `CopyResource`，更新前台组与对应 MC 相机，把纹理绑定到 `MCWORLD`、`MCDEPTH`、`MCOVERLAY`。`finish_frame` 在效果使用完纹理后记录 GPU event，后续才能复用该组。

**输出与限制：** 输出是可供 ReShade 合成的三张纹理和同帧相机。当前消费者拒绝超过 3840×2160 的帧；高分辨率的 CPU/GPU 复制仍有成本。三缓冲降低等待，不保证两个独立游戏完全同步。

## 2. 深度遮挡、视角重投影和画面抖动

### 获取 CS 深度

[depth_capture.h](../source/cs2-bridge/native/depth_capture.h) 监听深度目标绑定、viewport、draw、clear。它按绘制顶点数乘实例数累计分数，选择符合画面比例的候选深度纹理；尺寸允许低至展示尺寸的约三分之一，以适配 FSR。

它只累计 viewport 深度范围约 `[0, 0.95]` 的世界绘制，避开单独的前景武器范围。切换渲染目标时保留候选引用，在深度清除前或合成开始前才复制，减少重复复制。D24/D32 都转为可采样视图；多重采样路径用 compute shader 对每个像素的 samples 取最小值，输出 R32_FLOAT。

`begin` 将结果绑定为 `CSDEPTH`，并发布 `HostDepthValid` 与 `HostDepthUv`。后者将有效 viewport 映射到纹理分配尺寸，避免 FSR 或裁剪区域导致深度位置错位。解析深度的 compute shader 使用后恢复此前绑定的 CS/SRV/UAV 状态。

### 合成与相机补偿

[MCPassthrough.fx](../source/cs2-bridge/shaders/MCPassthrough.fx) 的四个 pass 依次是 `Light → Composite → Bright → Final`。

`PS_Composite` 先将 MC reversed-Z 深度线性化；CS 深度先除以 `0.95`，再按 near/far 与 `HostReversedZ` 线性化。CS 单位已在宿主侧换成 MC 方块单位，因此两侧距离可比较。可见判定使用 `MC距离 < CS距离 + bias`，其中 bias 包含屏幕深度梯度项且有上限，缓解接缝闪烁。

MC 图像通常比 CS 当前相机滞后。[compositor.cpp](../source/cs2-bridge/native/compositor.cpp) 用当前宿主姿态和该张 MC 图像的姿态计算：

```text
R = 宿主相机方向到 MC 捕获相机方向的旋转
T = inverse(R_mc) * (host_position - mc_capture_position)
```

矩阵三行、平移、两侧 FOV 正切通过 `WarpRow0..2`、`WarpT`、`WarpTan` 传入 shader。开启重投影时，shader 将当前宿主的视线投到旧 MC 深度图：先做 24 次对数分布采样寻找表面交点，再做 3 次固定点细化；有效 MC 颜色才参加遮挡合成。`PosePrediction` 还能按最近两帧的位移和角度差做外推，角度差经过跨 360 度处理。

`Light` 从宿主画面取得模糊光色，合成时用受限的亮度增益、染色和远距离雾色调整 MC。`Final` 对整张合成世界施加 `Shake` 平移/旋转与 `PortalWarp`，再做接边柔化、屏幕空间接触阴影和 MC 发光。**overlay 最后使用原始 UV 叠加，因此 GUI 不随世界抖动或传送门扭曲。**

**输出与限制：** 成果是有遮挡关系的合成画面；关掉 `McActive` 时直接输出 CS 原画面。接触阴影是屏幕空间近似，重投影也无法补出旧图里没有的新露出表面。F8 的干员网格动作与这里的全屏 `Shake` 是两套实现，不能将一个功能的通过视为另一个也已通过。

## 3. 输入为什么需要事件队列，镜头怎么跟随

### 姿态共享与输入入口

[adapter.cpp](../source/cs2-bridge/native/adapter.cpp) 在 ReShade 回调中读取 `Local\MC_CS_Bridge_Pose`。头部 magic 为 `0x504D5343`，版本 1；`8` 是奇偶序列，`16` 是时间戳，`24` 是连接活动状态，`32..67` 是九个姿态 float。数据需在一秒内更新，复制前后序列必须一致。渲染回调另在 `1792` 起发布带序列号的实际渲染姿态快照，供另一端更及时地读取。

| 偏移 | 输入或镜头字段 |
| --- | --- |
| `28` | F7 切换的建造模式 |
| `96`、`104` | 按键状态位与快捷栏槽位 |
| `136` | MC GUI 是否打开 |
| `140/144` | 归一化鼠标坐标 |
| `148/152/156` | 鼠标按钮、滚轮、Shift |
| `160`、`164` | 32 位指针更新序号、窗口焦点 |
| `176/184` | 事件环形队列写/读计数 |
| `232/236/240` | 请求镜头模式、实际镜头模式、实际距离 |
| `1980/1984/1992` | MC 移动启用、移动键位掩码、输入版本 |
| `2000` | F8 请求计数 |

[ui_input.h](../source/cs2-bridge/native/ui_input.h) 的 `push` 在 `256` 起保存 64 个事件，每个 24 字节，即六个 int：`type,key,code,mods,down,extra`。写满时拒绝新事件，不覆盖消费者尚未读到的事件。键盘、字符、玩法按钮、指针、移动分别使用类型 1 到 5。

这里不能只传“此刻按下了什么”：渲染帧比服务器 tick 快，极短点击可能在两次 tick 之间完成。因此 `poll_gameplay`、`poll_movement` 同时观察 pressed/released，必要时将 down 与 up 都写入队列。失去活动状态后释放已记录的玩法/移动按键。GUI 第一次打开时还跳过现有按键，避免打开背包的 E 立即又把背包关闭。

鼠标位置直接采用 ReShade 已转换好的客户区坐标，再除以客户区宽高；不能再调用一次 `ScreenToClient`。`pointerSerial` 固定写 32 位，因为紧邻的 `164` 是焦点标志，写 64 位会覆盖它。这两个细节分别对应点击偏移和“看似一直没焦点”的问题。

### 桥接玩家的第三人称

F5 只在桥接活动、游戏有焦点、建造模式且没有 GUI 时，将请求模式循环为 0/1/2。[AvatarCamera.cs](../source/cs2-bridge/plugin/AvatarCamera.cs) 的 `UpdateAvatarCamera` 读取它，创建 `point_camera`，使用玩家的输入角 `V_angle` 计算方向：模式 1 在眼睛后方 160 CS 单位，模式 2 在前方 160 单位并反转朝向。服务端将 camera 赋给 `CameraServices.ViewEntity`，同时发布实际位置、模式与距离。

防穿墙不是简单限制后退距离。`SafeAvatarCamera` 用中心和八个角点共九条射线，测试静态地图三角形，再与扩张了镜头半径的 MC 方块 AABB 做 slab 相交，找到安全距离；命中时给表面留出余量。局部玩家本体和附件按需要隐藏，原效果标志、颜色和渲染模式会保存，在释放时恢复；只恢复自己接管的 `ViewEntity`，避免覆盖之后由别的系统切换的视角。

**输出与限制：** 这段代码实现的是 MC 桥接玩家镜头。它不等同于完整的 Demo spectator 鼠标环绕控制器，源码不能证明“按空格切第三人称后必定固定背后并随鼠标绕行”已经实现或验收。Demo 拍摄仍需单独检查观战模式、观察目标切换、鼠标输入及镜头回收。

## 4. MC 方块怎样挡住 CS 玩家，重同步怎样防止掉落

### 坐标与地图表面

[WorldGeometry.cs](../source/cs2-bridge/plugin/WorldGeometry.cs) 统一了坐标变换：

```text
MC = (CS.x / 40, CS.z / 40 + 96, -CS.y / 40)
CS = (MC.x * 40, -MC.z * 40, (MC.y - 96) * 40)
```

所以一个 MC 方块对应 40 CS 单位，同时换轴并反转一条轴。静态地图三角形来自使用者在本地生成的缓存；加载时检查 magic `0x3152544d` 和三角形数上限。构建 BVH 时按最长轴的三角形中心划分，叶子最多 12 个三角形。`Ray` 先裁剪 AABB，再做三角形相交，供镜头和其它几何查询使用。`SurfaceCells` 将地图表面采样为附近可用的屏障体素，地面略向下偏移以落在脚下格子。

### 从 VoxelShape 到原生碰撞体

[WorldBridge.java](../source/universal-modder/examples/minecraft-gta5-passthrough/mc/src/main/java/dev/rehan/passthrough/WorldBridge.java) 的 `flush` 获取 `BlockState.getCollisionShape(...).toAabbs()`，通过 `t:"blocks"` 发送：

```text
shapes:    [[x,y,z, minX,minY,minZ,maxX,maxY,maxZ, ...], ...]
materials: [[x,y,z,"minecraft:block_id"], ...]
set/clear: [x,y,z, ...]  // 兼容旧接收端的整方块协议
```

每个 AABB 是相对格子的局部坐标；`[x,y,z]` 表示清空碰撞。活塞碰撞可伸出格子，所以不能把边界截到 `[0,1]`。方块变化也把六邻居放入更新集合，因为栅栏和墙的形状依赖相邻方块；移动活塞会逐 tick 重算。

MC 每 tick 处理最多 512 格，每消息最多 128 格并限制约 48 KiB 字符。单格超过 32 个 AABB 时降为整体包围盒。形状与材质都未改变时不重复发送。

[BlockColliders.cs](../source/cs2-bridge/plugin/BlockColliders.cs) 的 `ApplyBlockUpdates` 优先使用 `shapes`，缺少形状的旧 `set` 才回退为完整方块。`TileCollisionBoxes` 取 AABB 最短边作为小立方体边长，沿三轴铺设；每轴最后一块向内重叠，使并集恰好覆盖该盒子，不让薄门或活塞头膨胀成整方块。

每块小立方体对应一个隐藏的 `prop_physics_override`。代码先放到最终位置再 `DispatchSpawn`，按本机 CubeModel 的原生碰撞边长计算 `SetScale`，之后 `DisableMotion` 并再次定位。原因是缩放可能重建物理体，而在原点出生后才搬过去曾导致新平台短时间不参与玩家碰撞。

**输出与限制：** CS 得到真实原生物理体，MC 保留视觉表现。预算为全局最多 2048 个碰撞体、单格最多 128 个；超限会保留已有状态并告警，不能把“收到消息”直接当作附近所有形状都已成功创建。过于复杂的 MC 形状使用保守包围盒，也会牺牲精度。

### 重同步是一个有截止时间的事务

[MinecraftBridge.cs](../source/cs2-bridge/plugin/MinecraftBridge.cs) 在清除旧实体前调用 [BlockResync.cs](../source/cs2-bridge/plugin/BlockResync.cs) 的 `BeginBlockResync`。该路径针对当前由 CS 驱动的存活玩家；MC 移动路径另有自己的状态管理。

1. 先保存 controller/pawn 标识、本次生命和地图、位置、速度、移动类型及本工具是否加了冻结位；再设置 `MOVETYPE_NONE`、`FL_FROZEN`，将玩家保持在锚点。保存必须早于修改，异常时才有可恢复的原值。
2. 下帧发 `{"t":"blocksync","id":...,"r":24,"center":[...]}`。中心由 CS 实际脚下位置换算，不依赖可能滞后的 MC 视角。
3. MC 用同一 `id` 回复 `blocksbegin`，优先发送脚下附近 3×3×3 格，再按同心方环从近到远扫描。扫描预算每 tick 4096 格，高度范围为中心 -24 到 +40，最多收集 3000 个目标方块。
4. 扫描完成且该次 pending 形状全部发出后，才在同一有序传输中发 `blocksready`。新扫描取代旧扫描时发 `blockscancel`。
5. CS 只接受当前 `id`，完成或取消都释放 hold。正常 tick 检查和独立 3 秒 timer 双重兜底；重复请求不会无限延长最初截止时间。断线、死亡、换 pawn、换地图或外部位移超过 128 CS 单位都会中止相应恢复流程。
6. 释放先清掉 hold 所有权，再只恢复仍属同一玩家生命的状态；只撤销自己添加的冻结位，也不将已被其它系统传送的玩家拉回旧锚点。

**输出与限制：** 该事务解决“清旧碰撞到新碰撞到齐之间脚下暂时变空”的问题。`blocksready.limited` 或碰撞体预算不足仍表示可能只有局部恢复；三秒到期选择释放玩家，避免变成永久冻结，不代表整个扫描范围已完整重建。

## 5. F8 怎样对原始干员网格做动作

### 开关入口和两种运行路径

[adapter.cpp](../source/cs2-bridge/native/adapter.cpp) 的 `after_present` 每帧调用 `poll_dance_key` 和 `pawn_dance_native::begin`。这是关键位置：如果只放在 `before_effects`，关闭 ReShade effects 后按键和动作状态就不会前进。按键要求当前游戏窗口有焦点；短按通过边沿与 `pendingDanceTaps` 保留。

- **有桥接服务端：** F8 累加姿态共享内存 `2000` 的请求计数。[SquashDance.cs](../source/cs2-bridge/plugin/SquashDance.cs) 的 `TickDance` 消费计数差，检查焦点、GUI 与 addon 心跳，以奇数次点击切换状态。它收集受支持的存活 T 方机器人，向 `Local\MC_CS_PawnDance` 发布最多 64 名 actor。
- **离线本地回放/预览：** 没有有效桥接时，命令行必须含 `-insecure`，并有 `-mccs-spectator-dance` 或 `+sv_lan 1` 才允许本地切换。此模式只在渲染端授权已验证世界模型，不向服务端发送请求，也不依赖 MC 机器人列表。

动作共享内存为 4096 字节：`0` magic `0x50444E43`、`4` 版本 2、`8` 奇偶序列、`16` 心跳、`24` 开关、`28` actor 数、`32` 起始 Unix 毫秒、`40` BPM、`44` 可选单 pawn 诊断目标。actor 表从 `128` 开始，每条 32 字节，包含 pawn、模型 kind、位置、原有缩放、朝向。原生端读取前 3072 字节的一致性快照，心跳超过 1.5 秒或字段非法就不启用动作。

本地预览用 `GetTickCount64` 推进 126 BPM 相位；服务端模式用共享的起始时间推进。这意味着 **暂停 Demo 时间线不自动暂停动作相位**。F8 当前是开启/关闭动作，关闭后原始 draw 恢复；若需要“停在某个动作姿势”，还需独立相位暂停机制。

### 识别、变换与绘制

[pawn_dance_native.h](../source/cs2-bridge/native/pawn_dance_native.h) 不生成替身人物，不修改服务器骨骼，也不把原人物隐藏后换模型。它识别原始 draw，在原 VS 已完成蒙皮后插入 geometry shader，变换输出三角形。

识别分两层。网格以字节数、stride 与前 4096 字节指纹匹配，本地生成的 UV mask 可让不应参与身份判断的字节归零。着色器按原始字节码的精确哈希读取预编译缓存，再通过反射定位 `g_instanceBuffer`、`g_transformBuffer` 与常量缓冲布局。未知网格、未知 shader 或不支持的拓扑保持原 draw。

实例表要求 stride 32，骨骼变换表要求 stride 48（三行 float4）。代码从 instance 的骨骼基址加本地生成的 root offset 读取根位置。`pawn_dance_compute.h` 观察原有 compute 蒙皮 dispatch 的变换 SRV，让后续深度 pass 也能取得当前帧的骨架；它自身不修改或读回骨骼缓冲。draw 还检查 context 和 frame 一致，拒绝上一帧遗留的表。

相位 `phase` 对应的压缩量为：

```text
q = 0.5 - 0.5 * cos(2*pi*phase)
scale = (1 + 0.55*q, 1 + 0.55*q, 1 - 0.82*q)
yaw = (phase mod 4) * pi/2
newWorld = root + rotateZ((oldWorld - root) * scale, yaw)
```

[pawn_dance_shader.h](../source/cs2-bridge/native/pawn_dance_shader.h) 使用相同 root 处理一名人物的各个材质。非均匀缩放后的法线按逆缩放再旋转并归一化，切线重新与法线正交；世界位置重新投影，同时更新裁剪平面和相关输出。只改屏幕位置会让光照、裁剪和附件各走各的，因此这些量必须一起改。

### 黑影为什么要同时处理深度和遮罩

只移动颜色 pass 时，原来的深度/遮罩仍可能留在未变形位置，表现为原姿态黑影、轮廓或遮挡。`compile_depth` 支持已核验的深度输出签名：没有世界坐标 varying 的 pass 由 `projectionToWorld` 重建世界坐标，再应用同一根位置变换并重新投影。

另一类已核验的三字段 mask 签名使用 `TEXCOORD3` 保存世界位置、`TEXCOORD6` 保存投影 z/w。变换时两者与 `SV_Position` 同步更新，并保留 primitive ID。武器/手套附件的几何 pass 也有对应颜色、深度和 mask 路径。

**输出与限制：** 修复目标是同一模型所有已识别 pass 在同一位置。这里依赖精确 shader 布局与本地模型表；遇到未知变体会跳过，所以不能根据已实现该分支就宣称所有警方黑影已消失。完整验收还要覆盖 CT 身体/头眼、不同材质、阴影/遮罩、F8 开关和观察目标切换。

## 6. 手套与 C4 为什么需要“找主人”

附件不能围绕自己的局部原点压缩旋转，否则身体转了，手套或背部 C4 仍留在旧位置。[pawn_dance_native.h](../source/cs2-bridge/native/pawn_dance_native.h) 每帧清空并重新收集最多 128 个经过网格核验的身体实例，记录 `instance,kind,instances SRV,transforms SRV`。深度 draw 也能提前建立候选。

匹配只允许同一帧、同一 context、相同 SRV 底层范围的实例表与变换表。附件先画、身体还没出现时保持附件原状；不从上一帧猜主人，也不随便选择最近的人。

### 世界手套

[pawn_dance_glove_shader.h](../source/cs2-bridge/native/pawn_dance_glove_shader.h) 使用本地生成的左右手绑定点与骨骼 offset。compute shader 将手套左右手点分别变到世界坐标，再同每个身体的左右手点比较，两边的平方距离都必须小于 `0.0001`。得到的动作轴心是身体 root，不是手套骨架本身。

一次 `Dispatch(1,1,1)` 使用 128 个线程并行检查候选，再归并结果。身体不同材质可能有不同 instance ID，却指向同一骨骼 palette，因此以骨骼基址与 root offset 去重；出现两个不同骨架都匹配则判为歧义、不执行变换。输出一个 float4：xyz 为 root，w 为是否有效，供 GS 的 `t16` 使用。

世界 `w_` 手套有独立指纹白名单；默认手套还有专门 skeleton 路径。代码不会仅凭“看起来像手套”就授权第一人称 `v_` 手套。当前手套归属 shader 的 `allowAllVerifiedInstances` 门槛偏向本地预览路径，不能推断它覆盖全部桥接服务端模式。

### 武器、背部附件与 C4

[pawn_dance_weapon_shader.h](../source/cs2-bridge/native/pawn_dance_weapon_shader.h) 采用同样的 GPU 归属思路，但匹配点是本地模型表中的 held/holster 点和身体 socket。武器点经过武器 root 变换，身体 socket 经过其骨骼变换；只有对应点平方距离小于 `0.0001` 且实例标志、缩放、类型与 actor 授权有效时才接受。

C4 的显示跟随由这套附件机制处理，并非按实体名称每帧硬搬到玩家位置。背负与持握是否可用取决于该 C4 网格在本地表里是否有正确的点位，以及该绘制变体是否有缓存。得到唯一有效 root 后，附件使用与身体相同的 scale/yaw，连同深度与遮罩一起变换。owner 查找每个附件 draw 只运行一次，不在每个三角形里重复扫描候选。

**输出与限制：** 附件在有可靠归属时共享身体动作轴心。无候选、歧义、不同表范围、未知指纹或签名都会跳过，并累计 skip reason。地面掉落武器或已放置 C4 不应仅因离人近就跟随。它也不等同于 C4 爆炸/MC 世界事件联动，后者属于游戏逻辑层。

## 7. 为避免卡死做了哪些具体改造

| 风险来源 | 目前代码的处理 | 仍需注意 |
| --- | --- | --- |
| draw 中反射生成并编译大量变换程序 | 使用精确哈希预编译缓存；[build-dance-shader-cache.cpp](../tools/build-dance-shader-cache.cpp) 在离线工具里调用 factory | 原生 pipeline 初始化仍会做必要反射与创建 GPU 对象，未知变体不自动编译补全 |
| 读取 GPU 缓冲等待完成 | [pawn_dance_readback.h](../source/cs2-bridge/native/pawn_dance_readback.h) 分帧排队，`Map(DO_NOT_WAIT)`，同项每帧只轮询一次 | 只用于有界的网格/实例识别数据；拒绝结构化和 dynamic 缓冲，不读回动画骨骼表 |
| 无上限诊断堆积 | 队列最多 64 项、总 2 MiB、每帧新增 8 项，单项最大 512 KiB | 到预算即延迟识别，不能为了立刻匹配改回忙等 |
| 每个三角形搜索所有附件主人 | GPU compute 一次解析 owner，GS 读单个 float4 | 每个附件 draw 仍有 dispatch 与状态切换成本 |
| 自己补画再次进入拦截回调 | `insideDraw` RAII guard 跳过重入 | 不支持已有 GS 或 HS/DS 的 draw，直接保留原绘制 |
| 资源释放同步触发回调并再次获取同一锁 | 原生资源表使用递归锁；资源销毁时删除识别和待回读项 | 资源/锁路径仍应在驱动和地图切换条件下实测 |
| 临时绑定污染游戏后续 pass | `SavedConstants`/`SavedCompute` 保存并恢复 GS/CS 常量、SRV、UAV、shader 与 D3D11.1 常量范围 | 新增 pass 必须保持这个恢复约定 |
| effects 关闭后 F8 失效 | 输入与动作帧状态移到始终触发的 `after_present` | 焦点、离线参数和模块是否编入仍是必要条件 |
| 游戏/MC 帧率不同造成 CPU 等 GPU | MC 异步捕获，宿主三组纹理与非阻塞查询；忙时保留旧完整帧 | 代价可能是延迟/丢帧，不是零成本合成 |

旧的同步采集头文件不再接入 addon 正常渲染入口；不要为了补缓存把它重新挂回每次绘制。正常帧的耗时写入共享内存环形缓冲：`192` 是帧计数，`2048` 起有 128 个帧耗时，`2560` 起有准备/上传/effects 分段耗时。[sample-dance-frame-time.py](../tools/sample-dance-frame-time.py) 当前读取前一种帧耗时，输出均值、p95、最大值和估算 FPS；细分耗时仍需按上述布局读取。动作统计可由 [read-pawn-dance-diagnostics.py](../tools/read-pawn-dance-diagnostics.py) 查看。

显式深度 dump 仍含同步 staging 读取；`depth_capture.h` 和部分诊断入口保留开发机路径约定。它们不是正常每帧导出机制，移植前应检查路径和调用方式。这里记录的是降低已知阻塞风险的工程处理，不能给出“再也不会卡死”的保证。

## 8. 如何沿源码复现和验证

先按 [构建说明](build.md) 建起默认合成器、MC 模组和 CS 插件，验证姿态、三层图像、遮挡及输入。随后验证一个完整方块、一扇薄门、连接形状和移动活塞，再验证重同步完成、超时、死亡和断线恢复。应同时观察实际碰撞与图像，视觉位置正确不代表原生碰撞体已生效。

研究实验 F8 时，先看 [generate-agent-dance.py](../tools/generate-agent-dance.py)、[generate-weapon-dance.py](../tools/generate-weapon-dance.py)、[generate-glove-dance.py](../tools/generate-glove-dance.py) 的输入约定，在本机生成与目标游戏版本匹配的识别/骨骼表；再用离线缓存工具生成 `pawn_dance_cache.h`。四个所需文件为 `pawn_dance_agent_data.h`、`pawn_dance_weapon_data.h`、`pawn_dance_glove_data.h`、`pawn_dance_cache.h`。这些生成器源码本身不包含可供安装的游戏资源，也不自动替使用者下载它们。

### 可执行步骤与缺少的前置输入

下表列出脚本实际要求，避免在新电脑上只重复运行缺数据的命令。Python 生成器还依赖 NumPy；元数据里的数组、逆绑定矩阵、mesh/render 骨骼映射、buffer layout 与前缀字节都需来自匹配版本的本地资源分析，不能只填模型名称。

| 工具 | 必备本地输入 | 输出与成功判断 |
| --- | --- | --- |
| `generate-agent-dance.py --replay` | `build-cache/pawn-dance/agents/fingerprints.json`、`replay-fingerprints.json`；后者应包含默认 SAS | `pawn_dance_agent_data.h`、`replay-rig-report.json`；输出 rigs、fingerprints、anchors 和 rejected 计数 |
| `generate-weapon-dance.py --metadata ...` | 武器元数据 JSON，**以及已经存在的基线 `pawn_dance_weapon_data.h`** | 在原 mesh 索引后追加；生成 weapon-rig-report，检查 accepted/rejected 与所需 mask 位数 |
| `generate-glove-dance.py` | 上述 agent replay 报告及 `glove-fingerprints.json` | `pawn_dance_glove_data.h`、`glove-rig-report.json`；包含正负匹配与属性改写检查 |
| `build-dance-shader-cache.cmd` | 三张生成表和 `logs/pawn-dance-capture/*.dxbc` | `pawn_dance_cache.h`；末行必须为 `PASS cached body=... attachment=... glove=1` 且退出码为 0 |

agent 即使传入 `--metadata`，仍会读取固定路径下的 `fingerprints.json` 确定桥接基线顺序。glove 当前没有路径参数，且严格核验该开发版本的资源集合：8 个世界手套、14 个第一人称模型、16 条世界手套指纹；游戏版本不同而断言失败时应重新核实集合和骨架，而不是直接删除断言。

**当前武器生成器是扩展工具，不能在基线头文件缺失时从零运行。** 它先读取并解析已有表，保留旧 mesh 索引。仓库也不附原始资源提取结果和自动采集游戏 VS 的完整流水线；因此实验 F8 还不能在任意新电脑上仅靠 clone 后一条命令复现。基线表和资源元数据需要使用者本地准备，或另行实现从零初始化的生成步骤。默认关闭 F8 正是把这个边界显式化。

前置输入已准备好后，从仓库根目录运行以下命令；武器的 `--metadata` 路径按本地文件名替换：

```powershell
python tools/generate-agent-dance.py --replay
python tools/generate-weapon-dance.py --metadata build-cache/pawn-dance/replay-weapon-analysis/weapon-fingerprints.json
python tools/generate-glove-dance.py
```

不要只检查头文件存在。先看各 JSON 报告中的模型路径、kind、骨骼 root、指纹数量、rejected 原因与输入 SHA-256；同一模型表内 kind 顺序必须与 shader 生成时一致。以上命令应顺序执行；glove 依赖 agent 的 replay 报告。

接着在 Visual Studio 的 **x64 Native Tools Command Prompt** 中，从仓库根运行：

```bat
call tools\build-dance-shader-cache.cmd
if errorlevel 1 exit /b 1
call tools\test-pawn-depth.cmd
if errorlevel 1 exit /b 1
set MCCS_ENABLE_EXPERIMENTAL_DANCE=1
call source\cs2-bridge\native\build-msvc.cmd
```

`build-dance-shader-cache.cmd` 会将工具编译到 `build-cache/pawn-dance/replay-test`，默认读取 `logs/pawn-dance-capture`。其 EXE 本身也支持显式参数 `build-dance-shader-cache.exe <capture-folder> <output.h>`。失败时可能留下不完整输出文件，不能凭“文件已经生成”继续启用。默认原生构建还要求配置 ReShade SDK，见 [构建说明](build.md)。上面的 `set` 是 cmd 语法；不要原样当成 PowerShell 环境变量赋值。

只想检查非阻塞回读队列时，可独立运行 [run-readback-check.cmd](../tests/rendering/run-readback-check.cmd)：它用合成缓冲和 WARP，无需游戏或本地模型表。这条检查与依赖真实 shader 缓存的 `test-pawn-depth.cmd` 要分开理解。

游戏启动后，用以下只读命令观察，最好在相同场景分别保存 F8 开/关数据：

```powershell
python tools/read-pawn-dance-diagnostics.py
python tools/sample-dance-frame-time.py
```

诊断里的 `body_programs`、`weapon_programs` 表示缓存命中并创建的程序数，draw/skip 是累计计数，`owner_candidates` 是某一帧的候选数；应比较开关前后的增量。`on`/`actors` 来自服务端发布区，本地 Demo 模式并不更新这个服务端开关，不能仅以 `on=0` 判断本地 F8 没运行。共享内存不存在时工具会报告未运行，不应由诊断程序创建一个空映射伪装成连接成功。

[test-pawn-depth.cpp](../tools/test-pawn-depth.cpp) 提供离线 GPU 验证入口，可以检查特定深度/遮罩变换的输出，但不能取代真实回放。实际验收至少分别覆盖动作关闭/开启/关闭恢复、Demo 暂停与跳转、T/CT、头眼和身体、默认/独立手套、持握/背负/掉落 C4、换观察目标，以及前后耗时分布。对于大厅，也只对离线入口和识别条件满足的 draw 有可能生效，不能仅凭按了 F8 就宣称整个准备界面已适配。

当前发布的是可检查的实现与已知边界；未完成的黑影、全附件覆盖和 Demo 鼠标镜头验收应继续记录为待验证项。
