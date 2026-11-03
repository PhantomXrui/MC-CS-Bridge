using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using V3=System.Numerics.Vector3;

namespace MinecraftBridge;
public sealed partial class MinecraftBridgePlugin {
    sealed class MinecraftMovementLease {
        public required CCSPlayerController Player;
        public required uint Pawn;
        public required long Life, Session;
        public required string Map;
        public required MoveType_t Move, ActualMove;
        public required V3 Anchor;
        public V3 Position, Eye, RenderEye, Velocity;
        public long Sequence=-1,ReceivedAt,CreatedAt=Environment.TickCount64;
        public bool Ready,Grounded,Sneak,Sprint,Passenger;
        public int VehicleId=-1;
        public string Pose="STANDING";
    }
    MinecraftMovementLease? minecraftMovement;
    bool minecraftMovementEnabled=true;
    long nextMovementSession=Environment.TickCount64<<16;
    ulong previousMovementKeys=ulong.MaxValue;
    long movementRecoveryAt;
    string? movementLastError;
    ulong? movementTestKeys;
    readonly Queue<object> minecraftMovementTrace=new();
    bool MinecraftMovementActive => minecraftMovement!=null;
    bool SameMovementPawn(MinecraftMovementLease m,CCSPlayerPawn pawn)=>m.Player.IsValid && m.Player.PawnIsAlive &&
        m.Player.PlayerPawn.Raw==m.Pawn && pawn.EntityHandle.Raw==m.Pawn && Server.MapName==m.Map && ActorLife(m.Player)==m.Life;

    void ReleaseMinecraftMovement(string reason) {
        var m=minecraftMovement;minecraftMovement=null;previousMovementKeys=ulong.MaxValue;movementTestKeys=null;minecraftMovementTrace.Clear();
        view?.Write(1980,0);
        if(m==null)return;
        SendMovementKeys(0,true,m.Session);
        Send(new {t="movement_off",session=m.Session});
        try {
            if(m.Player.PlayerPawn.Value is {IsValid:true} pawn && SameMovementPawn(m,pawn)) {
                if(pawn.MoveType==MoveType_t.MOVETYPE_NONE)pawn.MoveType=m.Move;
                if(pawn.ActualMoveType==MoveType_t.MOVETYPE_NONE)pawn.ActualMoveType=m.ActualMove;
                Utilities.SetStateChanged(pawn,"CBaseEntity","m_MoveType");
            }
        }catch(Exception e){Log("MC movement release: "+e.Message);}
        Log("MC movement released: "+reason);
    }
    void TickMinecraftMovement(CCSPlayerController player,CCSPlayerPawn pawn,bool allowed) {
        if(!minecraftMovementEnabled || !allowed || !player.PawnIsAlive || pawn.AbsOrigin==null || view!.ReadInt32(1992)!=1) {ReleaseMinecraftMovement("inactive");return;}
        if(minecraftMovement is {} old && !SameMovementPawn(old,pawn))ReleaseMinecraftMovement("life_changed");
        if(minecraftMovement==null) {
            if(blockResyncHold!=null || Environment.TickCount64<movementRecoveryAt)return;
            var anchor=WorldGeometry.ToMC(Vec(pawn.AbsOrigin));
            minecraftMovement=new MinecraftMovementLease{Player=player,Pawn=player.PlayerPawn.Raw,Life=ActorLife(player),Session=++nextMovementSession,Map=Server.MapName,Move=pawn.MoveType,ActualMove=pawn.ActualMoveType,Anchor=anchor,Position=anchor,Eye=anchor+new V3(0,pawn.ViewOffset.Z/Units,0),RenderEye=anchor+new V3(0,pawn.ViewOffset.Z/Units,0)};
            previousMovementKeys=ulong.MaxValue;
            Log($"MC movement session {minecraftMovement.Session}: Minecraft owns physics; host anchor {anchor}");
        }
        var m=minecraftMovement;
        if((m.Ready && Environment.TickCount64-m.ReceivedAt>2000) || (!m.Ready && Environment.TickCount64-m.CreatedAt>15000)){
            movementLastError=m.Ready?"state_timeout":"handshake_timeout";
            ReleaseMinecraftMovement(movementLastError);movementRecoveryAt=Environment.TickCount64+2000;return;
        }
        // Do not use FL_FROZEN: it also suppresses native mouse/command processing.
        // CS has no locomotion simulation here. It is a damageable body following MC.
        if(pawn.MoveType!=MoveType_t.MOVETYPE_NONE || pawn.ActualMoveType!=MoveType_t.MOVETYPE_NONE){
            pawn.MoveType=MoveType_t.MOVETYPE_NONE;pawn.ActualMoveType=MoveType_t.MOVETYPE_NONE;
            Utilities.SetStateChanged(pawn,"CBaseEntity","m_MoveType");
        }
        var position=WorldGeometry.ToCS(m.Position);
        pawn.Teleport(new Vector(position.X,position.Y,position.Z),null,new Vector(0,0,0));
        view?.Write(1980,1);
    }
    static bool ReadMovementVector(JsonElement message,string name,out V3 value) {
        value=default;
        if(!message.TryGetProperty(name,out var a)||a.ValueKind!=JsonValueKind.Array||a.GetArrayLength()!=3)return false;
        try{value=new(a[0].GetSingle(),a[1].GetSingle(),a[2].GetSingle());}catch{return false;}
        return float.IsFinite(value.X)&&float.IsFinite(value.Y)&&float.IsFinite(value.Z)&&MathF.Abs(value.X)<25000&&MathF.Abs(value.Y)<25000&&MathF.Abs(value.Z)<25000;
    }
    static bool MovementBool(JsonElement m,string name)=>m.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.True;
    void ApplyMinecraftMovement(JsonElement state,CCSPlayerPawn pawn) {
        var m=minecraftMovement;
        if(m==null||!SameMovementPawn(m,pawn)||!state.TryGetProperty("session",out var session)||!session.TryGetInt64(out long id)||id!=m.Session||!MovementBool(state,"ready"))return;
        if(!state.TryGetProperty("seq",out var serial)||!serial.TryGetInt64(out long sequence)||sequence<=m.Sequence)return;
        if(!ReadMovementVector(state,"p",out var feet)||!ReadMovementVector(state,"eye",out var eye)||!ReadMovementVector(state,"v",out var velocity))return;
        bool hasRenderEye=ReadMovementVector(state,"renderEye",out var renderEye);
        if(!hasRenderEye)renderEye=m.Ready?m.RenderEye:eye;
        // Reject malformed eye offsets, not legitimate MC teleports/vehicles.
        if(V3.DistanceSquared(eye,feet)>64 || (hasRenderEye && V3.DistanceSquared(renderEye,eye)>64))return;
        m.Position=feet;m.Eye=eye;m.RenderEye=renderEye;m.Velocity=velocity;m.Sequence=sequence;m.ReceivedAt=Environment.TickCount64;m.Ready=true;
        m.Grounded=MovementBool(state,"grounded");m.Sneak=MovementBool(state,"sneak");m.Sprint=MovementBool(state,"sprint");m.Passenger=MovementBool(state,"passenger");
        if(state.TryGetProperty("pose",out var pose)&&pose.ValueKind==JsonValueKind.String)m.Pose=pose.GetString()??"STANDING";
        if(state.TryGetProperty("vehicleId",out var vehicle)&&vehicle.TryGetInt32(out int vehicleId))m.VehicleId=vehicleId;
        if(testing){minecraftMovementTrace.Enqueue(new {ms=Environment.TickCount64,sequence,p=new[]{feet.X,feet.Y,feet.Z},v=new[]{velocity.X,velocity.Y,velocity.Z},m.Grounded,m.Sneak,m.Sprint,m.Passenger,m.Pose});while(minecraftMovementTrace.Count>512)minecraftMovementTrace.Dequeue();}
    }
    void SendMovementKeys(ulong keys,bool force=false,long? session=null) {
        string[] names=["forward","back","left","right","jump","sneak","sprint"];
        for(int i=0;i<names.Length;i++)if(force||((keys^previousMovementKeys)&(1ul<<i))!=0)Send(new{t="move",session=session??minecraftMovement?.Session??0,k=names[i],down=(keys&(1ul<<i))!=0});
        previousMovementKeys=keys;
    }
    void UpdateMinecraftMovementInput(bool focused,bool menu) {
        if(minecraftMovement==null)return;
        // Keep a full held-state heartbeat as well as the ordered tap events.
        ulong keys=testing&&movementTestKeys.HasValue?movementTestKeys.Value:focused&&!menu?(ulong)view!.ReadInt64(1984):0;
        SendMovementKeys(keys,frame%16==0);
    }
    object MinecraftMovementSnapshot()=>minecraftMovement is {} m?new {
        enabled=minecraftMovementEnabled,active=true,ready=m.Ready,session=m.Session,seq=m.Sequence,lastError=movementLastError,
        ageMs=m.ReceivedAt==0?-1:Environment.TickCount64-m.ReceivedAt,
        p=new[]{m.Position.X,m.Position.Y,m.Position.Z},eye=new[]{m.Eye.X,m.Eye.Y,m.Eye.Z},renderEye=new[]{m.RenderEye.X,m.RenderEye.Y,m.RenderEye.Z},
        v=new[]{m.Velocity.X,m.Velocity.Y,m.Velocity.Z},m.Grounded,m.Sneak,m.Sprint,m.Passenger,m.VehicleId,m.Pose,
        csMovement=m.Player.PlayerPawn.Value?.MoveType.ToString(),inputMask=previousMovementKeys,inputVersion=view?.ReadInt32(1992),trace=minecraftMovementTrace.ToArray()
    }:(object)new {enabled=minecraftMovementEnabled,active=false,lastError=movementLastError,recoveryInMs=Math.Max(0,movementRecoveryAt-Environment.TickCount64)};
}
