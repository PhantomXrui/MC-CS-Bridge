using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using V3=System.Numerics.Vector3;

namespace MinecraftBridge;
public sealed partial class MinecraftBridgePlugin {
    sealed class ActorHold {
        public required CCSPlayerController Player;
        public required uint Controller;
        public required uint Pawn;
        public required long Life;
        public required int Team;
        public required MoveType_t Move,ActualMove;
        public required bool BotActive;
        public float Duck;
        public bool DuckOverride;
        public long Received;
        public long Started;
        public V3 Position;
        public float Yaw;
        public string Mode="leash";
    }
    sealed record PendingBirth(long Seq,uint First,long FirstLife,uint Second,long SecondLife,HashSet<uint> Existing,int OriginalBots,V3 Position,long Deadline,string? Failure=null);
    // 玩家仪式（末地烛）触发的单亲生成：不要求两只 T，只要一个坐标。
    sealed record SoloBirth(long Seq,HashSet<uint> Existing,int OriginalBots,V3 Position,long Deadline);
    SoloBirth? soloBirth;
    long lastSoloBirthSeq;
    readonly Dictionary<uint,ActorHold> actorHolds=new();
    readonly Dictionary<long,long> actorControlSequences=new(),actorBreedCooldowns=new();
    sealed class ChildActor {
        public required CCSPlayerController Player;
        public uint Controller,Pawn;
        public float Scale,AbsScale,ClientScale,Radius,Capsule,ViewZ;
        public V3 Mins,Maxs;
        public V3 BirthPosition;
    }
    readonly Dictionary<uint,ChildActor> childActors=new();
    const float ChildScale=.5f;
    const int BreedingTBotLimit=20; // Adults and children share this limit; does not prefill bots.
    PendingBirth? pendingBirth;
    long lastBreedSequence;
    long interactionSceneGeneration;
    bool interactionSandbox;
    bool interactionUseTestSpawns;
    readonly Dictionary<uint,int> interactionAdultSlots=new();
    static readonly V3[] InteractionSafePositions=[new(220,2400,-119.8f),new(140,2400,-119.8f)];

    void ClearInteractionSandbox(){
        interactionSandbox=false;interactionUseTestSpawns=false;interactionAdultSlots.Clear();interactionSceneGeneration++;
        Server.ExecuteCommand("bot_ignore_players 0");
    }
    void HandleInteractionSpawn(CCSPlayerController player){
        if(!interactionSandbox||!player.IsValid||!player.IsBot||player.TeamNum!=2)return;
        uint controller=player.EntityHandle.Raw,pawnHandle=player.PlayerPawn.Raw;
        long generation=interactionSceneGeneration;
        Server.NextFrame(()=>{
            if(!interactionSandbox||generation!=interactionSceneGeneration||!player.IsValid||!player.IsBot
                ||player.EntityHandle.Raw!=controller||player.PlayerPawn.Raw!=pawnHandle||player.TeamNum!=2
                ||!player.PawnIsAlive||player.PlayerPawn.Value is not {IsValid:true} pawn)return;
            V3 at;
            if(childActors.TryGetValue(player.Index,out var child)&&child.Controller==controller)at=child.BirthPosition;
            else {
                if(!interactionUseTestSpawns)return; // Ordinary agents use CS's own T spawn selection.
                // A newly requested child is not an adult. Its birth completion chooses its position.
                if(pendingBirth is {} pending&&!pending.Existing.Contains(controller))return;
                var live=Utilities.GetPlayers().Where(p=>p.IsValid&&p.IsBot&&p.TeamNum==2).Select(p=>p.EntityHandle.Raw).ToHashSet();
                foreach(uint stale in interactionAdultSlots.Keys.Where(raw=>!live.Contains(raw)).ToArray())interactionAdultSlots.Remove(stale);
                if(!interactionAdultSlots.TryGetValue(controller,out int slot)){
                    slot=Enumerable.Range(0,InteractionSafePositions.Length).FirstOrDefault(i=>!interactionAdultSlots.ContainsValue(i),-1);
                    if(slot<0)return;
                    interactionAdultSlots[controller]=slot;
                }
                at=InteractionSafePositions[slot];
            }
            pawn.Teleport(new Vector(at.X,at.Y,at.Z),new QAngle(0,0,0),new Vector(0,0,0));
            Log($"MC interaction sandbox spawn bot={player.Index} juvenile={IsJuvenile(player)} position={at}");
        });
    }

    CCSPlayerController? FindActor(int index,long life,int team)=>Utilities.GetPlayers().FirstOrDefault(p=>p.IsValid&&p.IsBot&&p.TeamNum==team&&p.PawnIsAlive&&p.Index==index&&!mobProxies.ContainsKey(p.Index)&&ActorLife(p)==life);
    bool IsJuvenile(CCSPlayerController player)=>childActors.TryGetValue(player.Index,out var child)&&player.EntityHandle.Raw==child.Controller;
    float ActorScale(CCSPlayerController player)=>IsJuvenile(player)?ChildScale:1f;
    object ActorChildrenSnapshot()=>childActors.Where(pair=>pair.Value.Player.IsValid&&pair.Value.Player.EntityHandle.Raw==pair.Value.Controller).Select(pair=>{
        var child=pair.Value;var livePawn=child.Player.PlayerPawn.Value;var p=livePawn?.IsValid==true?livePawn:null;
        var node=p?.CBodyComponent?.SceneNode;
        return new {h=pair.Key,life=child.Player.IsValid?ActorLife(child.Player):0,juvenile=true,scale=node?.Scale,absScale=node?.AbsScale,clientLocalScale=node?.ClientLocalScale,
            position=p?.AbsOrigin==null?null:new[]{p.AbsOrigin.X,p.AbsOrigin.Y,p.AbsOrigin.Z},
            mins=p==null?null:new[]{p.Collision.Mins.X,p.Collision.Mins.Y,p.Collision.Mins.Z},
            maxs=p==null?null:new[]{p.Collision.Maxs.X,p.Collision.Maxs.Y,p.Collision.Maxs.Z},viewZ=p?.ViewOffset.Z,
            radius=p?.Collision.BoundingRadius,capsuleRadius=p?.Collision.CapsuleRadius,
            surroundingMins=p==null?null:new[]{p.Collision.SurroundingMins.X,p.Collision.SurroundingMins.Y,p.Collision.SurroundingMins.Z},
            surroundingMaxs=p==null?null:new[]{p.Collision.SurroundingMaxs.X,p.Collision.SurroundingMaxs.Y,p.Collision.SurroundingMaxs.Z},
            specifiedMins=p==null?null:new[]{p.Collision.SpecifiedSurroundingMins.X,p.Collision.SpecifiedSurroundingMins.Y,p.Collision.SpecifiedSurroundingMins.Z},
            specifiedMaxs=p==null?null:new[]{p.Collision.SpecifiedSurroundingMaxs.X,p.Collision.SpecifiedSurroundingMaxs.Y,p.Collision.SpecifiedSurroundingMaxs.Z}};
    }).ToArray();
    void FinishBirthPopulation(PendingBirth pending,CCSPlayerController? keep=null)=>FinishBirthPopulation(pending.Existing,pending.OriginalBots,keep);
    void FinishBirthPopulation(HashSet<uint> existing,int originalBots,CCSPlayerController? keep=null){
        // Snapshot raw entity handles, not reusable indices. Exactly one new bot can be kept.
        foreach(var bot in Utilities.GetPlayers().Where(p=>p.IsValid&&p.IsBot&&!existing.Contains(p.EntityHandle.Raw)))
            if(keep==null||bot.EntityHandle.Raw!=keep.EntityHandle.Raw)bot.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
        Server.ExecuteCommand("bot_quota_mode normal");
        Server.ExecuteCommand("bot_join_team any");
        Server.ExecuteCommand("bot_quota "+(originalBots+(keep==null?0:1)));
    }
    // 插件侧入口：在指定坐标生成一只小 T（末地烛仪式 / 比特多胎共用）。
    void ApplySpawnJuvenile(JsonElement m){
        long seq=m.GetProperty("seq").GetInt64();
        if(seq<=lastSoloBirthSeq)return;lastSoloBirthSeq=seq;
        if(!connected||!mcReady||soloBirth!=null)return;
        var all=Utilities.GetPlayers();
        if(all.Count(p=>p.IsValid&&p.IsBot&&p.TeamNum==2)>=BreedingTBotLimit||all.Count>=Math.Min(24,Server.MaxPlayers))return;
        // 模组发来的是 MC 坐标，宿主内部用 CS 单位（用当前地图的 YOffset，不能用写死的 96）。
        var mc=new V3(m.GetProperty("x").GetSingle(),m.GetProperty("y").GetSingle(),m.GetProperty("z").GetSingle());
        var at=new V3(mc.X*Units,-mc.Z*Units,(mc.Y-YOffset)*Units);
        int count=all.Count(p=>p.IsValid&&p.IsBot);
        soloBirth=new SoloBirth(seq,all.Where(p=>p.IsValid).Select(p=>p.EntityHandle.Raw).ToHashSet(),count,at,Environment.TickCount64+4000);
        Server.ExecuteCommand("bot_quota_mode normal");Server.ExecuteCommand("bot_join_team T");Server.ExecuteCommand("bot_quota "+(count+1));
        Log($"Solo juvenile requested seq={seq} quota={count+1}");
    }
    void RejectActor(int handle,long life,string reason)=>Send(new{t="actorcontrolack",h=handle,life,ok=false,reason});
    void ApplyActorControl(JsonElement m) {
        int handle=m.GetProperty("h").GetInt32();long life=m.GetProperty("life").GetInt64(),seq=m.GetProperty("seq").GetInt64();
        string mode=m.GetProperty("mode").GetString()??"";
        var player=FindActor(handle,life,2)??(mode=="courtship"?null:FindActor(handle,life,3));
        if(player?.PlayerPawn.Value is not {} pawn||!connected||!mcReady){RejectActor(handle,life,"stale_or_wrong_team");return;}
        if(mode=="courtship"&&IsJuvenile(player)){RejectActor(handle,life,"juvenile");return;}
        if(seq<=actorControlSequences.GetValueOrDefault(life))return;
        actorControlSequences[life]=seq;
        if(mode=="release"){ReleaseActor(player.Index,"guest_release");return;}
        bool gathering=mode is "dance" or "dance_arrive";
        if(mode is not ("leash" or "boat" or "courtship" or "dance" or "dance_arrive") || gathering&&(!danceOn||player.TeamNum!=2)){RejectActor(handle,life,"mode");return;}
        var a=m.GetProperty("p");if(a.GetArrayLength()!=3)return;
        var at=new V3(a[0].GetSingle(),a[1].GetSingle(),a[2].GetSingle());float yaw=m.GetProperty("yaw").GetSingle();
        if(!float.IsFinite(at.X)||!float.IsFinite(at.Y)||!float.IsFinite(at.Z)||!float.IsFinite(yaw))return;
        var next=WorldGeometry.ToCS(at);var origin=pawn.AbsOrigin;if(origin==null)return;
        var current=new V3(origin.X,origin.Y,origin.Z);long now=Environment.TickCount64;
        actorHolds.TryGetValue(player.Index,out var hold);
        if(hold!=null&&(hold.Life!=life||hold.Controller!=player.EntityHandle.Raw||hold.Pawn!=player.PlayerPawn.Raw)){
            ReleaseActor(player.Index,"life_replaced");hold=null;
        }
        float dt=hold==null?.05f:Math.Clamp((now-hold.Received)/1000f,.01f,.5f);
        // Mounting moves the passenger from beside the boat to its seat in one MC tick.
        float limit=hold==null||mode=="boat"&&hold.Mode!="boat"?3f:1f+12f*dt;
        bool arrival=mode=="dance_arrive";
        if(arrival){
            // Validate against the command's fixed origin, never the moving player's position.
            if(danceGatherOrigin is not {} gatherOrigin||V3.Distance(next,gatherOrigin)>32*Units||geometry==null
                ||!geometry.Ray(next+new V3(0,0,12),next-new V3(0,0,16),out _)){
                RejectActor(handle,life,"gather_destination");return;
            }
        }
        if(!arrival&&V3.Distance(current,next)>limit*Units){ReleaseActor(player.Index,"speed_limit");RejectActor(handle,life,"speed_limit");return;}
        // Guest movement may never tunnel a native CS wall. The exact host triangle cache remains authoritative.
        if(!arrival&&geometry!=null&&V3.DistanceSquared(current,next)>.01f&&geometry.Ray(current+new V3(0,0,24),next+new V3(0,0,24),out _)){
            ReleaseActor(player.Index,"host_wall");RejectActor(handle,life,"host_wall");return;
        }
        if(hold==null){
            if(actorHolds.Count>=(gathering?64:12)){RejectActor(handle,life,"limit");return;}
            var movement=pawn.MovementServices?.As<CCSPlayer_MovementServices>();
            hold=new ActorHold{Player=player,Controller=player.EntityHandle.Raw,Pawn=player.PlayerPawn.Raw,Life=life,Team=player.TeamNum,Started=now,Move=pawn.MoveType,ActualMove=pawn.ActualMoveType,BotActive=pawn.BotAllowActive,Duck=movement?.DuckAmount??0,DuckOverride=movement?.DuckOverride??false};
            actorHolds[player.Index]=hold;Log($"MC actor control begin bot={handle} life={life} mode={mode}");
        }
        if(hold.Mode!=mode)hold.Started=now;
        hold.Received=now;hold.Position=next;hold.Yaw=-yaw-90;hold.Mode=mode;
        MoveControlledActor(hold,pawn);
    }
    bool IsControlledActorAttacker(uint pawnHandle)=>actorHolds.Values.Any(h=>h.Pawn==pawnHandle
        &&Environment.TickCount64-h.Received<=500&&h.Player.IsValid&&h.Player.PawnIsAlive
        &&h.Player.EntityHandle.Raw==h.Controller&&h.Player.PlayerPawn.Raw==h.Pawn
        &&h.Player.TeamNum==h.Team&&ActorLife(h.Player)==h.Life);
    readonly HashSet<uint> witherBarrage=new();
    readonly Dictionary<uint,long> witherWeaponRetry=new();
    void TickWitherBarrage(List<CCSPlayerController> players){
        var previous=witherBarrage.ToHashSet();witherBarrage.Clear();
        foreach(var bot in players.Where(p=>p.IsValid&&p.IsBot&&p.TeamNum==2&&p.PawnIsAlive)){
            var pawn=bot.PlayerPawn.Value;var ai=pawn?.Bot;if(pawn?.AbsOrigin==null||ai==null)continue;
            if(previous.Contains(bot.Index)){ai.ButtonFlags&=~1UL;ai.LookAtSpotAttack=false;ai.IsRapidFiring=false;}
            if(actorHolds.ContainsKey(bot.Index)||IsJuvenile(bot))continue;
            var eye=WorldGeometry.ToMC(new V3(pawn.AbsOrigin.X,pawn.AbsOrigin.Y,pawn.AbsOrigin.Z+pawn.ViewOffset.Z));
            var target=mobs.Where(m=>IsBarrageBoss(m.Key))
                .OrderBy(m=>V3.DistanceSquared(eye,m.Value)).FirstOrDefault(m=>V3.DistanceSquared(eye,MobAim(m.Key,m.Value))<96*96&&!Occluded(eye,MobAim(m.Key,m.Value)));
            if(target.Equals(default(KeyValuePair<int,V3>)))continue;
            var weapon=pawn.WeaponServices?.ActiveWeapon.Value;
            if(weapon==null||weapon.DesignerName.Contains("knife")){
                if(Environment.TickCount64>=witherWeaponRetry.GetValueOrDefault(bot.Index)){
                    witherWeaponRetry[bot.Index]=Environment.TickCount64+2000;
                    var rifle=pawn.WeaponServices?.MyWeapons.Select(h=>h.Value).FirstOrDefault(w=>w?.DesignerName=="weapon_ak47");
                    if(rifle==null)bot.GiveNamedItem("weapon_ak47");
                    bot.ExecuteClientCommand("slot1");
                }
                continue;
            }
            var aim=WorldGeometry.ToCS(MobAim(target.Key,target.Value));
            var delta=aim-new V3(pawn.AbsOrigin.X,pawn.AbsOrigin.Y,pawn.AbsOrigin.Z+pawn.ViewOffset.Z);
            float yaw=MathF.Atan2(delta.Y,delta.X)*180/MathF.PI,pitch=-MathF.Atan2(delta.Z,MathF.Sqrt(delta.X*delta.X+delta.Y*delta.Y))*180/MathF.PI;
            pawn.Teleport(null,new QAngle(pitch,yaw,0),null);
            SetActorVector(ai.LookAtSpot,aim);ai.LookAtSpotAttack=true;ai.LookAtSpotTimestamp=Server.CurrentTime;ai.LookAtSpotDuration=.2f;
            ai.IsAimingAtEnemy=true;ai.IsRapidFiring=true;ai.ButtonFlags=(ai.ButtonFlags&~8192UL)|1UL;
            if(weapon.Clip1<10){weapon.Clip1=30;Utilities.SetStateChanged(weapon,"CBasePlayerWeapon","m_iClip1");}
            witherBarrage.Add(bot.Index);
        }
    }
    bool IsPeacefulActorAttacker(uint pawnHandle)=>interactionSandbox&&Utilities.GetPlayers().Any(p=>
        p.IsValid&&p.IsBot&&p.PawnIsAlive&&p.PlayerPawn.Raw==pawnHandle);
    void MoveControlledActor(ActorHold hold,CCSPlayerPawn pawn){
        pawn.BotAllowActive=false;pawn.MoveType=MoveType_t.MOVETYPE_NONE;pawn.ActualMoveType=MoveType_t.MOVETYPE_NONE;
        var movement=pawn.MovementServices?.As<CCSPlayer_MovementServices>();
        if(movement!=null){movement.DuckOverride=hold.Mode=="boat";movement.DuckAmount=hold.Mode=="boat"?1:hold.Duck;}
        // Repeated native Teleport calls invalidate interpolation/attachments even for an
        // unchanged position. A dance hold only needs a teleport when it actually moves.
        var origin=pawn.AbsOrigin;
        bool stationaryDance=hold.Mode is "dance" or "dance_arrive";
        if(!stationaryDance||origin==null||V3.DistanceSquared(hold.Position,new V3(origin.X,origin.Y,origin.Z))>.01f)
            pawn.Teleport(new Vector(hold.Position.X,hold.Position.Y,hold.Position.Z),new QAngle(0,hold.Yaw,0),new Vector(0,0,0));
        var weapon=pawn.WeaponServices?.ActiveWeapon.Value;
        if(weapon!=null){weapon.NextPrimaryAttackTick=Server.TickCount+4;weapon.NextSecondaryAttackTick=Server.TickCount+4;}
    }
    void ReleaseActor(uint index,string reason){
        if(!actorHolds.Remove(index,out var hold))return;
        if(hold.Player.IsValid&&hold.Player.EntityHandle.Raw==hold.Controller&&hold.Player.PlayerPawn.Raw==hold.Pawn
                &&ActorLife(hold.Player)==hold.Life&&hold.Player.PlayerPawn.Value is {IsValid:true} pawn){
            pawn.BotAllowActive=hold.BotActive;pawn.MoveType=hold.Move;pawn.ActualMoveType=hold.ActualMove;
            var movement=pawn.MovementServices?.As<CCSPlayer_MovementServices>();
            if(movement!=null){movement.DuckOverride=hold.DuckOverride;movement.DuckAmount=hold.Duck;}
        }
        Log($"MC actor control end bot={index} reason={reason}");
    }
    void ClearActorControls(){
        foreach(var bot in Utilities.GetPlayers().Where(p=>p.IsValid&&witherBarrage.Contains(p.Index)))
            if(bot.PlayerPawn.Value?.Bot is {} ai){ai.ButtonFlags&=~1UL;ai.LookAtSpotAttack=false;ai.IsRapidFiring=false;}
        witherBarrage.Clear();witherWeaponRetry.Clear();
        foreach(uint id in actorHolds.Keys.ToArray())ReleaseActor(id,"reset");
        foreach(var child in childActors.Values)RestoreChild(child);
        childActors.Clear();
        if(pendingBirth is {} pending){FinishBirthPopulation(pending);Send(new{t="actorbreedack",seq=pending.Seq,ok=false,reason="reset"});}
        pendingBirth=null;actorControlSequences.Clear();actorBreedCooldowns.Clear();lastBreedSequence=0;
    }
    void ApplyActorBreed(JsonElement m,CCSPlayerController local){
        long seq=m.GetProperty("seq").GetInt64();
        if(seq<=lastBreedSequence)return;lastBreedSequence=seq;
        void Reject(string reason)=>Send(new{t="actorbreedack",seq,ok=false,reason});
        if(!connected||!mcReady||pendingBirth!=null){Reject("busy");return;}
        var first=FindActor(m.GetProperty("h").GetInt32(),m.GetProperty("life").GetInt64(),2);
        var second=FindActor(m.GetProperty("mate").GetInt32(),m.GetProperty("mateLife").GetInt64(),2);
        if(first==null||second==null||first==second){Reject("parents");return;}
        if(IsJuvenile(first)||IsJuvenile(second)){Reject("juvenile");return;}
        long now=Environment.TickCount64,life=ActorLife(first),mateLife=ActorLife(second);
        if(!actorHolds.TryGetValue(first.Index,out var firstHold)||!actorHolds.TryGetValue(second.Index,out var secondHold)
            ||firstHold.Mode!="courtship"||secondHold.Mode!="courtship"||now-firstHold.Started<2500||now-secondHold.Started<2500){Reject("courtship_required");return;}
        if(actorBreedCooldowns.GetValueOrDefault(life)>now||actorBreedCooldowns.GetValueOrDefault(mateLife)>now){Reject("cooldown");return;}
        var a=first.PlayerPawn.Value!.AbsOrigin;var b=second.PlayerPawn.Value!.AbsOrigin;var player=local.PlayerPawn.Value?.AbsOrigin;
        if(a==null||b==null||player==null){Reject("position");return;}
        var av=new V3(a.X,a.Y,a.Z);var bv=new V3(b.X,b.Y,b.Z);var pv=new V3(player.X,player.Y,player.Z);
        if(V3.Distance(av,bv)>.95f*Units||Math.Min(V3.Distance(av,pv),V3.Distance(bv,pv))>12*Units){Reject("distance");return;}
        var all=Utilities.GetPlayers();if(all.Count(p=>p.IsValid&&p.IsBot&&p.TeamNum==2)>=BreedingTBotLimit||all.Count>=Math.Min(24,Server.MaxPlayers)){Reject("population_limit");return;}
        int count=all.Count(p=>p.IsValid&&p.IsBot);
        pendingBirth=new PendingBirth(seq,first.Index,life,second.Index,mateLife,all.Where(p=>p.IsValid).Select(p=>p.EntityHandle.Raw).ToHashSet(),count,(av+bv)/2,now+4000);
        // bot_add can also increase quota and race the quota filler. Request only one quota slot.
        Server.ExecuteCommand("bot_quota_mode normal");Server.ExecuteCommand("bot_join_team T");Server.ExecuteCommand("bot_quota "+(count+1));
        Log($"MC actor breed requested T parents={first.Index},{second.Index} seq={seq} quota={count+1}");
    }
    void TickActorInteractions(List<CCSPlayerController> players){
        TickWitherBarrage(players);
        long now=Environment.TickCount64;
        if(interactionSandbox)foreach(var bot in players.Where(p=>p.IsValid&&p.IsBot&&p.PawnIsAlive)){
            if(witherBarrage.Contains(bot.Index))continue;
            var weapon=bot.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
            if(weapon!=null){weapon.NextPrimaryAttackTick=Server.TickCount+4;weapon.NextSecondaryAttackTick=Server.TickCount+4;}
        }
        TickChildren();
        TickJuvenileAi(players);
        foreach(var pair in actorHolds.ToArray()){
            var h=pair.Value;
            if(now-h.Received>500||!h.Player.IsValid||h.Player.EntityHandle.Raw!=h.Controller||!h.Player.PawnIsAlive
                    ||h.Player.TeamNum!=h.Team||h.Player.PlayerPawn.Raw!=h.Pawn||ActorLife(h.Player)!=h.Life){ReleaseActor(pair.Key,"expired_or_respawned");continue;}
            if(h.Player.PlayerPawn.Value is {IsValid:true} pawn)MoveControlledActor(h,pawn);
        }
        if(soloBirth is {} solo){
            var soloChild=players.FirstOrDefault(p=>p.IsValid&&p.IsBot&&p.TeamNum==2&&!solo.Existing.Contains(p.EntityHandle.Raw));
            if(soloChild?.PlayerPawn.Value is {IsValid:true} soloPawn&&soloChild.PawnIsAlive){
                if(RegisterChild(soloChild,solo.Position)){
                    soloPawn.Teleport(new Vector(solo.Position.X,solo.Position.Y,solo.Position.Z),null,new Vector(0,0,0));
                    FinishBirthPopulation(solo.Existing,solo.OriginalBots,soloChild);
                    actorBreedCooldowns[ActorLife(soloChild)]=now+60000;
                    Send(new{t="juvenileack",seq=solo.Seq,ok=true,h=soloChild.Index,life=ActorLife(soloChild),juvenile=true,scale=ChildScale});
                    Log($"Solo juvenile success child={soloChild.Index}");
                } else {
                    FinishBirthPopulation(solo.Existing,solo.OriginalBots);
                    Send(new{t="juvenileack",seq=solo.Seq,ok=false,reason="child_scale_failed"});
                }
                soloBirth=null;
            } else if(now>solo.Deadline){
                FinishBirthPopulation(solo.Existing,solo.OriginalBots);
                Send(new{t="juvenileack",seq=solo.Seq,ok=false,reason="spawn_timeout"});
                soloBirth=null;
            }
        }
        if(pendingBirth is not {} pending)return;
        var child=players.FirstOrDefault(p=>p.IsValid&&p.IsBot&&p.TeamNum==2&&!pending.Existing.Contains(p.EntityHandle.Raw));
        if(pending.Failure==null&&(FindActor((int)pending.First,pending.FirstLife,2)==null||FindActor((int)pending.Second,pending.SecondLife,2)==null
                ||!actorHolds.ContainsKey(pending.First)||!actorHolds.ContainsKey(pending.Second))){
            pending=pending with{Failure="parents_changed"};pendingBirth=pending;
            Send(new{t="actorbreedack",seq=pending.Seq,ok=false,reason=pending.Failure});
            Log($"MC actor breed cancelled parents changed seq={pending.Seq}");
        }
        if(pending.Failure!=null){
            FinishBirthPopulation(pending);pendingBirth=null;
            return;
        }
        if(child?.PlayerPawn.Value is {IsValid:true} childPawn&&child.PawnIsAlive){
            V3 birthPosition=pending.Position+new V3(0,0,8);
            if(!RegisterChild(child,birthPosition)){
                FinishBirthPopulation(pending);
                Send(new{t="actorbreedack",seq=pending.Seq,ok=false,reason="child_scale_failed"});pendingBirth=null;return;
            }
            childPawn.Teleport(new Vector(birthPosition.X,birthPosition.Y,birthPosition.Z),null,new Vector(0,0,0));
            FinishBirthPopulation(pending,child);
            actorBreedCooldowns[pending.FirstLife]=now+60000;actorBreedCooldowns[pending.SecondLife]=now+60000;
            actorBreedCooldowns[ActorLife(child)]=now+60000;
            Send(new{t="actorbreedack",seq=pending.Seq,ok=true,h=child.Index,life=ActorLife(child),juvenile=true,scale=ChildScale});
            Log($"MC actor breed success child={child.Index} parents={pending.First},{pending.Second}");pendingBirth=null;
        }else if(now>pending.Deadline){FinishBirthPopulation(pending);Send(new{t="actorbreedack",seq=pending.Seq,ok=false,reason="spawn_timeout"});pendingBirth=null;}
    }
    void ApplyCTInteractionMode(CCSPlayerController player){
        long generation=++interactionSceneGeneration;
        interactionSandbox=true;interactionUseTestSpawns=true;interactionAdultSlots.Clear();
        ClearActorControls();Server.ExecuteCommand("bot_quota 0");Server.ExecuteCommand("bot_kick");
        player.ChangeTeam(CsTeam.CounterTerrorist);Server.ExecuteCommand("exec mccs_ct_interactions");
        Server.ExecuteCommand("bot_quota_mode normal");Server.ExecuteCommand("bot_quota 0");
        AddTimer(.4f,()=>WaitForInteractionSceneEmpty(player,generation,0));
    }
    void WaitForInteractionSceneEmpty(CCSPlayerController player,long generation,int attempt){
        if(generation!=interactionSceneGeneration||!player.IsValid||!SupportedMap(Server.MapName))return;
        if(Utilities.GetPlayers().Any(p=>p.IsValid&&p.IsBot)){
            if(attempt>=20){Log("MC interaction scene could not clear old bots");ClearInteractionSandbox();return;}
            Server.ExecuteCommand("bot_quota 0");Server.ExecuteCommand("bot_kick");
            AddTimer(.25f,()=>WaitForInteractionSceneEmpty(player,generation,attempt+1));return;
        }
        player.Respawn();Server.ExecuteCommand("bot_join_team T");Server.ExecuteCommand("bot_quota 2");
        AddTimer(.25f,()=>WaitForInteractionSceneBots(player,generation,0));
    }
    void WaitForInteractionSceneBots(CCSPlayerController player,long generation,int attempt){
        if(generation!=interactionSceneGeneration||!player.IsValid||!SupportedMap(Server.MapName))return;
        var bots=Utilities.GetPlayers().Where(p=>p.IsValid&&p.IsBot).OrderBy(p=>p.Index).ToArray();
        if(bots.Length<2||bots.Take(2).Any(p=>!p.PawnIsAlive)){
            if(attempt>=40){Log("MC interaction scene quota fill timed out");Server.ExecuteCommand("bot_join_team any");ClearInteractionSandbox();return;}
            AddTimer(.25f,()=>WaitForInteractionSceneBots(player,generation,attempt+1));return;
        }
        foreach(var extra in bots.Skip(2))extra.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
        for(int i=0;i<2;i++){
            var team=CsTeam.Terrorist;
            if(bots[i].TeamNum!=(int)team){bots[i].ChangeTeam(team);bots[i].Respawn();}
            if(i<2){interactionAdultSlots[bots[i].EntityHandle.Raw]=i;HandleInteractionSpawn(bots[i]);}
        }
        Server.ExecuteCommand("bot_join_team T");Server.ExecuteCommand("bot_quota 2");
        Log($"MC interaction scene ready: T={bots[0].Index},{bots[1].Index} quota=2");
    }

    static V3 CopyActorVector(Vector v)=>new(v.X,v.Y,v.Z);
    static void SetActorVector(Vector target,V3 value){target.X=value.X;target.Y=value.Y;target.Z=value.Z;}
    void CaptureChild(ChildActor child,CCSPlayerPawn pawn){
        var node=pawn.CBodyComponent?.SceneNode??throw new InvalidOperationException("Child pawn has no scene node");
        child.Pawn=child.Player.PlayerPawn.Raw;
        child.Scale=node.Scale;child.AbsScale=node.AbsScale;child.ClientScale=node.ClientLocalScale;
        child.Mins=CopyActorVector(pawn.Collision.Mins);child.Maxs=CopyActorVector(pawn.Collision.Maxs);
        child.Radius=pawn.Collision.BoundingRadius;child.Capsule=pawn.Collision.CapsuleRadius;child.ViewZ=pawn.ViewOffset.Z;
    }
    bool RegisterChild(CCSPlayerController player,V3 birthPosition){
        var child=new ChildActor{Player=player,Controller=player.EntityHandle.Raw,BirthPosition=birthPosition};
        try{
            CaptureChild(child,player.PlayerPawn.Value!);childActors[player.Index]=child;
            ApplyChildScale(child,player.PlayerPawn.Value!,true);
            Log($"MC juvenile created bot={player.Index} scale={ChildScale} mins={child.Mins*ChildScale} maxs={child.Maxs*ChildScale}");
            return true;
        }catch(Exception e){RestoreChild(child);childActors.Remove(player.Index);Log("MC child scaling failed: "+e.Message);return false;}
    }
    void ApplyChildScale(ChildActor child,CCSPlayerPawn pawn,bool input=false){
        var node=pawn.CBodyComponent?.SceneNode;if(node==null)return;
        float scale=child.Scale*ChildScale;
        bool changed=Math.Abs(node.Scale-scale)>.001f||V3.DistanceSquared(CopyActorVector(pawn.Collision.Maxs),child.Maxs*ChildScale)>.001f;
        if(input)pawn.AcceptInput("SetScale",pawn,pawn,scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        node.Scale=scale;node.AbsScale=child.AbsScale*ChildScale;node.ClientLocalScale=child.ClientScale*ChildScale;
        SetActorVector(pawn.Collision.Mins,child.Mins*ChildScale);SetActorVector(pawn.Collision.Maxs,child.Maxs*ChildScale);
        pawn.Collision.BoundingRadius=child.Radius*ChildScale;
        if(child.Capsule>0)pawn.Collision.CapsuleRadius=child.Capsule*ChildScale;
        pawn.ViewOffset.Z=child.ViewZ*ChildScale;
        if(changed||input){Utilities.SetStateChanged(pawn,"CBaseEntity","m_CBodyComponent");Utilities.SetStateChanged(pawn,"CBaseModelEntity","m_Collision");}
    }
    void RestoreChild(ChildActor child){
        if(!child.Player.IsValid||child.Player.EntityHandle.Raw!=child.Controller||child.Player.PlayerPawn.Raw!=child.Pawn)return;
        var pawn=child.Player.PlayerPawn.Value;if(pawn?.IsValid!=true)return;
        var node=pawn.CBodyComponent?.SceneNode;if(node==null)return;
        pawn.AcceptInput("SetScale",pawn,pawn,child.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        node.Scale=child.Scale;node.AbsScale=child.AbsScale;node.ClientLocalScale=child.ClientScale;
        SetActorVector(pawn.Collision.Mins,child.Mins);SetActorVector(pawn.Collision.Maxs,child.Maxs);
        pawn.Collision.BoundingRadius=child.Radius;pawn.Collision.CapsuleRadius=child.Capsule;pawn.ViewOffset.Z=child.ViewZ;
        Utilities.SetStateChanged(pawn,"CBaseEntity","m_CBodyComponent");Utilities.SetStateChanged(pawn,"CBaseModelEntity","m_Collision");
    }
    void TickChildren(){
        foreach(var pair in childActors.ToArray()){
            var child=pair.Value;var player=child.Player;
            if(!player.IsValid||player.EntityHandle.Raw!=child.Controller||!player.IsBot||player.TeamNum!=2){RestoreChild(child);childActors.Remove(pair.Key);continue;}
            if(!player.PawnIsAlive||player.PlayerPawn.Value is not {IsValid:true} pawn)continue;
            // The identity follows this bot through respawns; never write an old pawn's bounds into a new one.
            if(player.PlayerPawn.Raw!=child.Pawn){CaptureChild(child,pawn);ApplyChildScale(child,pawn,true);}
            else ApplyChildScale(child,pawn);
        }
    }
}
