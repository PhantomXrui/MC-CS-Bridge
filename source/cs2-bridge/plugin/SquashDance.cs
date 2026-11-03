using System.IO.MemoryMappedFiles;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace MinecraftBridge;

public sealed partial class MinecraftBridgePlugin
{
    const double DanceBpm=126;
    const int DanceMaxActors=64;
    readonly record struct DanceActor(uint Pawn,int Kind,float X,float Y,float Z,float Scale,float Yaw);
    readonly List<DanceActor> dancingActors=new(DanceMaxActors);
    readonly record struct DanceAccessory(uint Pawn,uint Entity,string Model,float X,float Y,float Z);
    readonly List<DanceAccessory> danceAccessories=new(128);
    readonly List<DanceAccessory> danceAccessoryOrigins=new(56);
    readonly HashSet<(uint,float,float,float)> danceAccessoryPositions=new();
    MemoryMappedFile? danceMap;
    MemoryMappedViewAccessor? danceView;
    ulong danceSequence;
    bool danceOn;
    bool watermarkFollow;
    const string WatermarkText="Bilibili:Phantom_rui";
    long danceStartMs;
    long danceGatherRequest;
    System.Numerics.Vector3? danceGatherOrigin;
    uint observedDanceRequest;
    string? danceError;

    void LoadDanceNative()
    {
        observedDanceRequest=view!.ReadUInt32(2000);
        try {
            danceMap=MemoryMappedFile.CreateOrOpen(@"Local\MC_CS_PawnDance",4096);
            danceView=danceMap.CreateViewAccessor();
            danceSequence=danceView.ReadUInt64(8)&~1UL;
            danceView.Write(44,0u); // Clear any explicit single-pawn diagnostic selection on reload.
            PublishDance();
            Log("Original animated pawn dance publisher ready; F8 toggle");
        } catch(Exception e) when(e is IOException or UnauthorizedAccessException) {
            danceError=e.Message;Log("Native dance IPC unavailable: "+e.Message);
            danceView?.Dispose();danceView=null;danceMap?.Dispose();danceMap=null;
        }
        AddCommand("css_mccs_dance","Toggle original animated model squash dance (F8)",(player,info)=>{
            var humans=Utilities.GetPlayers().Where(p=>p.IsValid&&!p.IsBot).ToArray();
            if(humans.Length!=1 || !SupportedMap(Server.MapName) || !connected)return;
            string? address=humans[0].IpAddress;
            if(!string.IsNullOrEmpty(address)&&!address.StartsWith("127.")&&!address.StartsWith("loopback")&&address!="::1")return;
            SetDance(!danceOn,humans[0]);
        });
        AddCommand("css_mccs_gather","Gather once near the player, then dance in place",(player,info)=>{
            if(!LocalUniqueTSession()||!connected)return;
            var human=Utilities.GetPlayers().FirstOrDefault(p=>p.IsValid&&!p.IsBot&&p.PawnIsAlive);
            var at=human?.PlayerPawn.Value?.AbsOrigin;if(at==null)return;
            SetDance(true,human);if(!danceOn)return;
            danceGatherOrigin=new(at.X,at.Y,at.Z);
            danceGatherRequest=Math.Max(danceGatherRequest+1,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            SendDance();
            info.ReplyToCommand("[MC] 集合一次后原地跳舞；不会跟随你移动。F8 停止。");
        });
        AddCommand("css_mccs_dance_stick","Give the enchanted dance controller stick",(player,info)=>{
            if(!connected)return;
            SendDanceStick();
            info.ReplyToCommand("[MC] 已发放附魔木棍“舞蹈控制器”；右键 T 土匪开始/停止。");
        });
        AddCommand("css_mccs_watermark","Place the Phantom_rui watermark at the crosshair",(player,info)=>{
            PlaceWatermark(player);
            info.ReplyToCommand("[MC] 已在准星位置放置 Phantom_rui 水印。");
        });
        AddCommand("css_mccs_watermark_follow","Toggle an ID watermark that follows your view",(player,info)=>{
            ToggleFollowWatermark(player);
            info.ReplyToCommand("[MC] ID 跟随已切换。");
        });
        AddCommand("css_mccs_watermark_item","Give the Phantom_rui watermark name tag",(player,info)=>{
            if(!connected)return;
            GiveWatermarkItem();
            info.ReplyToCommand("[MC] 已发放 ID 名牌：右键放置固定水印，潜行+右键切换跟随。");
        });
        AddCommand("css_mccs_watermark_clear","Remove all Phantom_rui watermarks",(player,info)=>{
            ClearWatermark();
            info.ReplyToCommand("[MC] 已清除所有 Phantom_rui 水印。");
        });
    }
    bool NativeDanceReady()=>danceView!=null && danceView.ReadInt32(3072)==1 && danceView.ReadInt32(3308)==2
        && danceView.ReadInt32(3076)==0
        && Environment.TickCount64-danceView.ReadInt64(3080) is >=0 and <2000;
    void SendDance()=>Send(new{t="dance",on=danceOn,startMs=danceStartMs,bpm=DanceBpm,gatherRequest=danceGatherRequest});
    void SetDance(bool on,CCSPlayerController? player=null)
    {
        if(on && !NativeDanceReady()){
            danceError="Original pawn renderer is not ready";
            player?.PrintToChat("[MC] 干员原模型变形模块尚未就绪。");return;
        }
        if(on==danceOn)return;
        danceOn=on;danceError=null;
        if(on){danceStartMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();danceGatherRequest=0;danceGatherOrigin=null;}
        else {ReleaseDanceGather();ClearDanceActors();}
        PublishDance();SendDance();ShowDanceTitle(on);Log(on?"Original animated pawn dance ON":"Original animated pawn dance OFF");
        player?.PrintToChat(on?"[MC] 原模型压扁舞：已开启；F8恢复。":"[MC] 压扁舞已关闭，模型已恢复。");
    }
    void ShowDanceTitle(bool on)
    {
        string text=on?"开始":"结束";
        string color=on?"green":"red";
        // Action bar：血条/饥饿值上方的小字，不是居中大标题。
        Send(new{t="cmd",c="title @a actionbar {\"text\":\""+text+"\",\"color\":\""+color+"\"}"});
    }
    void SendDanceStick()
    {
        const string stick="minecraft:stick[minecraft:custom_name={text:\"舞蹈控制器\",italic:false,color:\"gold\"},minecraft:lore=[{text:\"右键 T 土匪：开始/停止压扁舞\",italic:false,color:\"gray\"}],minecraft:enchantments={\"minecraft:unbreaking\":1}]";
        Send(new{t="cmd",c="give @p "+stick+" 1"});
    }
    (float X,float Y,float Z) WatermarkPoint(float distance,float down)
    {
        double ry=watermarkYaw*Math.PI/180.0,rp=watermarkPitch*Math.PI/180.0;
        float x=watermarkOrigin[0]-(float)(Math.Sin(ry)*Math.Cos(rp)*distance);
        float y=watermarkOrigin[1]-(float)(Math.Sin(rp)*distance)-down;
        float z=watermarkOrigin[2]+(float)(Math.Cos(ry)*Math.Cos(rp)*distance);
        return (x,y,z);
    }
    static string Inv(float v)=>v.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture);
    string WatermarkSummon(float x,float y,float z,string tag,float scale)
        =>"summon minecraft:text_display "+Inv(x)+" "+Inv(y)+" "+Inv(z)
        +" {Tags:[\""+tag+"\"],text:{text:\""+WatermarkText+"\",color:\"white\"},background:0,shadow:1b,see_through:1b,billboard:\"center\",brightness:{block:15,sky:15},line_width:200,transformation:{left_rotation:[0f,0f,0f,1f],right_rotation:[0f,0f,0f,1f],translation:[0f,0f,0f],scale:["+Inv(scale)+"f,"+Inv(scale)+"f,"+Inv(scale)+"f]}}";
    void PlaceWatermark(CCSPlayerController? player=null)
    {
        if(!connected)return;
        var (x,y,z)=WatermarkPoint(2.5f,0f);
        Send(new{t="cmd",c=WatermarkSummon(x,y,z,"mccs_watermark",0.5f)});
        Log("Watermark placed at "+x+","+y+","+z);
        player?.PrintToChat("[MC] 已放置 ID 水印："+WatermarkText);
    }
    void ToggleFollowWatermark(CCSPlayerController? player=null)
    {
        if(!connected)return;
        watermarkFollow=!watermarkFollow;
        Send(new{t="cmd",c="kill @e[type=minecraft:text_display,tag=mccs_watermark_follow]"});
        if(watermarkFollow){
            var (x,y,z)=WatermarkPoint(1.8f,0.12f);
            Send(new{t="cmd",c=WatermarkSummon(x,y,z,"mccs_watermark_follow",0.4f)});
            player?.PrintToChat("[MC] ID 跟随：开（再按一次或 css_mccs_watermark_follow 关闭）");
        } else player?.PrintToChat("[MC] ID 跟随：关");
        Log("Watermark follow "+(watermarkFollow?"ON":"OFF"));
    }
    void UpdateFollowWatermark()
    {
        if(!watermarkFollow||!connected)return;
        var (x,y,z)=WatermarkPoint(1.8f,0.12f);
        Send(new{t="cmd",c="tp @e[type=minecraft:text_display,tag=mccs_watermark_follow,limit=1] "+Inv(x)+" "+Inv(y)+" "+Inv(z)});
    }
    void GiveWatermarkItem()
    {
        const string item="minecraft:name_tag[minecraft:custom_name={text:\"Bilibili:Phantom_rui\",italic:false,color:\"aqua\"},minecraft:lore=[{text:\"右键：放置固定 ID 水印\",italic:false,color:\"gray\"},{text:\"潜行+右键：ID 跟随视角\",italic:false,color:\"gray\"}]]";
        Send(new{t="cmd",c="give @p "+item+" 1"});
    }
    void ClearWatermark()
    {
        if(!connected)return;
        watermarkFollow=false;
        Send(new{t="cmd",c="kill @e[type=minecraft:text_display,tag=mccs_watermark]"});
        Send(new{t="cmd",c="kill @e[type=minecraft:text_display,tag=mccs_watermark_follow]"});
        Log("Watermarks cleared");
    }
    long lastRescueMs;
    /// 新地图的 MC 世界一开始没有地面（障碍块要等玩家站住才流式生成），
    /// 出生点悬空时玩家会直接掉出地图；这里把掉下去的人传送到最近的机器人旁边。
    void RescueFallenPlayer(List<CCSPlayerController> players,CCSPlayerController? human)
    {
        if(human==null||!human.IsValid||human.IsBot||!human.PawnIsAlive)return;
        var pp=human.PlayerPawn.Value;if(pp?.AbsOrigin is not {} at)return;
        // 掉出地图（Z 远低于地图）或停在世界原点（新建世界的默认出生点）都要救回。
        bool atOrigin=at.Length()<80f; // 距 CS 原点 2 格内：新建世界的默认出生点
        if(at.Z>-1500f&&!atOrigin)return;
        if(Environment.TickCount64-lastRescueMs<3000)return;
        var bot=players.FirstOrDefault(p=>p.IsBot&&p.PawnIsAlive&&p.PlayerPawn.Value?.AbsOrigin is not null);
        if(bot==null)return;
        var b=Position(bot.PlayerPawn.Value!.AbsOrigin!);
        var ci=System.Globalization.CultureInfo.InvariantCulture;
        Send(new{t="cmd",c="tp @p "+b[0].ToString("0.0",ci)+" "+(b[1]+1.5f).ToString("0.0",ci)+" "+b[2].ToString("0.0",ci)});
        lastRescueMs=Environment.TickCount64;
        Log("Rescued fallen player to a bot position");
    }
    void StopDance()
    {
        if(danceOn){danceOn=false;SendDance();}
        ReleaseDanceGather();ClearDanceActors();PublishDance();
        if(view!=null)observedDanceRequest=view.ReadUInt32(2000);
    }
    void UnloadDanceNative(){StopDance();danceView?.Dispose();danceView=null;danceMap?.Dispose();danceMap=null;}
    void ReleaseDanceGather(){foreach(var h in actorHolds.Where(p=>p.Value.Mode is "dance" or "dance_arrive").Select(p=>p.Key).ToArray())ReleaseActor(h,"dance_off");}
    void ClearDanceActors(){dancingActors.Clear();danceAccessories.Clear();danceAccessoryOrigins.Clear();danceAccessoryPositions.Clear();}
    static double DanceSquash(long now,long start)=>.5-.5*Math.Cos(2*Math.PI*((now-start)*DanceBpm/60000.0));
    void TickDance(CCSPlayerController player,List<CCSPlayerController> players)
    {
        uint request=view!.ReadUInt32(2000);
        if(request!=observedDanceRequest){
            uint taps=unchecked(request-observedDanceRequest);observedDanceRequest=request;
            if(taps<8 && (taps&1)!=0 && view.ReadInt32(164)!=0 && view.ReadInt32(136)==0
                && Environment.TickCount64-view.ReadInt64(112)<1000)SetDance(!danceOn,player);
        }
        if(danceOn && !NativeDanceReady()){
            danceError=$"Original pawn renderer unavailable (error={danceView?.ReadInt32(3076)}, heartbeatAgeMs={Environment.TickCount64-(danceView?.ReadInt64(3080)??0)})";
            Log(danceError);StopDance();return;
        }
        if(danceOn && frame%64==0)SendDance();
        ClearDanceActors();
        if(danceOn)foreach(var bot in players){
            if(!bot.IsValid || !bot.IsBot || bot.TeamNum!=2 || !bot.PawnIsAlive || mobProxies.ContainsKey(bot.Index))continue;
            var pawn=bot.PlayerPawn.Value;
            if(pawn?.AbsOrigin is not {} at || !pawn.IsValid)continue;
            string model=pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName??"";
            string normalizedModel=model.Replace('\\','/');
            if(!DanceModelKinds.TryGetValue(normalizedModel,out int kind))continue;
            // Modify only the original skinned draw; never pawn visibility or a substitute prop.
            dancingActors.Add(new(pawn.EntityHandle.Raw,kind,at.X,at.Y,at.Z,ActorScale(bot),pawn.AbsRotation?.Y??0));
            CollectDanceAccessories(pawn);
            if(dancingActors.Count==DanceMaxActors)break;
        }
        PublishDance();
    }
    void CollectDanceAccessories(CCSPlayerPawn pawn)
    {
        var seen=new HashSet<uint>();
        void Add(CBaseModelEntity entity){
            if(danceAccessories.Count>=256 || !entity.IsValid || !seen.Add(entity.EntityHandle.Raw) || entity.AbsOrigin is not {} p)return;
            string model=entity.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName??"";
            if(model.Length==0 || !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))return;
            var accessory=new DanceAccessory(pawn.EntityHandle.Raw,entity.EntityHandle.Raw,model,p.X,p.Y,p.Z);
            danceAccessories.Add(accessory);
            if(danceAccessoryOrigins.Count<56&&danceAccessoryPositions.Add((accessory.Pawn,p.X,p.Y,p.Z)))danceAccessoryOrigins.Add(accessory);
        }
        if(pawn.WeaponServices!=null)foreach(var handle in pawn.WeaponServices.MyWeapons)
            if(handle.Value is {IsValid:true} weapon)Add(weapon);
        var nodes=new Stack<CGameSceneNode>();var root=pawn.CBodyComponent?.SceneNode;
        if(root!=null&&root.Handle!=IntPtr.Zero)nodes.Push(root);
        int remaining=64;
        while(nodes.Count>0&&remaining-->0){
            var child=nodes.Pop().Child;
            for(int n=0;child!=null&&child.Handle!=IntPtr.Zero&&n<32;n++,child=child.NextSibling){
                if(child.Owner is {IsValid:true} owner && owner.DesignerName!="player")Add(owner.As<CBaseModelEntity>());
                nodes.Push(child);
            }
        }
    }
    void PublishDance()
    {
        if(danceView==null)return;
        danceView.Write(8,++danceSequence);Thread.MemoryBarrier();
        danceView.Write(0,0x50444E43u);danceView.Write(4,2u);
        danceView.Write(16,Environment.TickCount64);danceView.Write(24,danceOn?1:0);
        danceView.Write(28,danceOn?dancingActors.Count:0);
        danceView.Write(32,danceStartMs);danceView.Write(40,(float)DanceBpm);
        danceView.Write(48,danceOn?danceAccessoryOrigins.Count:0);
        if(danceOn)for(int i=0;i<dancingActors.Count;i++){
            var a=dancingActors[i];long offset=128+i*32;
            danceView.Write(offset,a.Pawn);danceView.Write(offset+4,a.Kind);
            danceView.Write(offset+8,a.X);danceView.Write(offset+12,a.Y);danceView.Write(offset+16,a.Z);
            danceView.Write(offset+20,a.Scale);danceView.Write(offset+24,a.Yaw);danceView.Write(offset+28,0u);
        }
        if(danceOn)for(int i=0;i<danceAccessoryOrigins.Count;i++){
            var a=danceAccessoryOrigins[i];long offset=2176+i*16;
            danceView.Write(offset,a.Pawn);danceView.Write(offset+4,a.X);danceView.Write(offset+8,a.Y);danceView.Write(offset+12,a.Z);
        }
        Thread.MemoryBarrier();danceView.Write(8,++danceSequence);
    }
    object DanceSnapshot()=>new{on=danceOn,startMs=danceStartMs,bpm=DanceBpm,key="F8",error=danceError,
        mode="original_skinned_mesh",nativeReady=NativeDanceReady(),
        nativeError=danceView?.ReadInt32(3076),nativeDraws=danceView?.ReadUInt64(3088),
        nativeMatchedActors=danceView?.ReadUInt32(3096),nativeSkippedDraws=danceView?.ReadUInt32(3100),
        diagnosticTarget=danceView?.ReadUInt32(44),
        accessories=danceAccessories.Select(a=>new{pawn=a.Pawn,entity=a.Entity,model=a.Model,origin=new[]{a.X,a.Y,a.Z}}),
        availableActors=Utilities.GetPlayers().Where(p=>p.IsValid&&p.IsBot&&p.TeamNum==2&&p.PawnIsAlive&&!mobProxies.ContainsKey(p.Index))
            .Select(p=>new{id=p.Index,pawn=p.PlayerPawn.Raw}),
        squash=danceOn?DanceSquash(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),danceStartMs):0,
        actors=dancingActors.Select(a=>new{pawn=a.Pawn,kind=a.Kind,foot=new[]{a.X,a.Y,a.Z},scale=a.Scale})};
}
