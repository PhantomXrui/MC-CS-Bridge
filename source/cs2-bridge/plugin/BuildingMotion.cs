using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using V3 = System.Numerics.Vector3;

namespace MinecraftBridge;
public sealed partial class MinecraftBridgePlugin {
    readonly Queue<object> motionTrace = new();
    (CCSPlayerPawn pawn, bool before)? testDuck;

    static V3 Vec(Vector v) => new(v.X,v.Y,v.Z);
    static bool HostSneaking(CCSPlayerController player, CCSPlayerPawn pawn) =>
        (player.Buttons & PlayerButtons.Duck) != 0 ||
        (pawn.MovementServices?.As<CCSPlayer_MovementServices>().DuckAmount ?? 0) > .1f;
    static float[] HostBody(CCSPlayerPawn pawn) {
        var lo=pawn.Collision.Mins;var hi=pawn.Collision.Maxs;
        return [lo.X/Units,lo.Z/Units,-hi.Y/Units,hi.X/Units,hi.Z/Units,-lo.Y/Units];
    }
    void ResetBuildingMotion() => motionTrace.Clear();
    void ReleaseTestDuck() {
        if(testDuck is {} held && held.pawn.IsValid && held.pawn.MovementServices != null)
            held.pawn.MovementServices.As<CCSPlayer_MovementServices>().DuckOverride=held.before;
        testDuck=null;
    }
    void SetTestDuck(CCSPlayerPawn pawn,bool on) {
        if(!on){ReleaseTestDuck();return;}
        if(pawn.MovementServices==null)return;
        var m=pawn.MovementServices.As<CCSPlayer_MovementServices>();
        if(testDuck?.pawn.EntityHandle.Raw!=pawn.EntityHandle.Raw){ReleaseTestDuck();testDuck=(pawn,m.DuckOverride);}
        m.DuckOverride=true;
    }
    // Read-only diagnostic sampling. Native CS owns movement, jumping and edge falling.
    // The cancelled MC edge guard and underfoot lift must not write player position/velocity.
    void TickBuildingMotion(CCSPlayerController player,CCSPlayerPawn pawn,bool active) {
        if(!testing || !active || pawn.AbsOrigin==null){ResetBuildingMotion();return;}
        var feet=Vec(pawn.AbsOrigin);
        motionTrace.Enqueue(new {tick=Server.TickCount,p=new[]{feet.X,feet.Y,feet.Z},v=new[]{pawn.AbsVelocity.X,pawn.AbsVelocity.Y,pawn.AbsVelocity.Z},flags=pawn.Flags,ground=pawn.GroundEntity.Raw,buttons=(ulong)player.Buttons});
        while(motionTrace.Count>512)motionTrace.Dequeue();
    }
    object BuildingMotionSnapshot(CCSPlayerController player,CCSPlayerPawn pawn)=>new{
        movementMode=MinecraftMovementActive?"minecraft":"native_cs",sneakEdgeAssist=false,underfootAssist=false,
        sneak=HostSneaking(player,pawn),grounded=(pawn.Flags&1)!=0,body=HostBody(pawn),
        duck=pawn.MovementServices?.As<CCSPlayer_MovementServices>().DuckAmount,
        eye=pawn.ViewOffset.Z/Units,velocity=new[]{pawn.AbsVelocity.X,pawn.AbsVelocity.Y,pawn.AbsVelocity.Z},
        trace=motionTrace.ToArray()};
}