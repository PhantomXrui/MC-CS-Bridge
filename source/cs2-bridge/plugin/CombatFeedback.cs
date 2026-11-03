using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using V3=System.Numerics.Vector3;

namespace MinecraftBridge;
public sealed partial class MinecraftBridgePlugin {
    const string BloodEffect="particles/blood_impact/blood_impact_basic.vpcf";
    bool shieldRaised;
    long shieldReceivedAt,lastShieldSound;
    int nativeDamageDepth;
    MemoryFunctionWithReturn<CEntityInstance,CTakeDamageInfo,int>? damageFunction;
    CCSPlayerController? scriptedAttacker;
    CCSPlayerPawn? scriptedVictim;
    int scriptedDamage;
    readonly HashSet<CParticleSystem> bloodEffects=new();
    void LoadCombatFeedback() {
        // The installed Feb-2025 server returns an int (verified in the local binary).
        // Its bundled API incorrectly exposes this as MemoryFunctionVoid: preserve the ABI.
        damageFunction=new(GameData.GetSignature("CBaseEntity_TakeDamageOld"));
        damageFunction.Hook(BeforeNativeDamage,HookMode.Pre);
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest=>manifest.AddResource(BloodEffect));
        RegisterEventHandler<EventPlayerHurt>((ev,info)=>{
            if(nativeDamageDepth>0 || ev.Userid is {IsBot:false})Log($"native player_hurt victim={ev.Userid?.Index} attacker={ev.Attacker?.Index} damage={ev.DmgHealth} health={ev.Health}");
            return HookResult.Continue;
        });
    }
    void UnloadCombatFeedback() {
        damageFunction?.Unhook(BeforeNativeDamage,HookMode.Pre);
        foreach(var fx in bloodEffects)if(fx.IsValid)fx.Remove();bloodEffects.Clear();
    }
    HookResult BeforeNativeDamage(DynamicHook hook) {
        var entity=hook.GetParam<CEntityInstance>(0);
        if(!entity.IsValid || entity.DesignerName!="player")return HookResult.Continue;
        var info=hook.GetParam<CTakeDamageInfo>(1);
        if(nativeDamageDepth>0 && scriptedVictim?.Handle==entity.Handle) {
            info.Attacker.Raw=scriptedAttacker?.PlayerPawn.Raw??uint.MaxValue;
            info.Damage=scriptedDamage;
            info.DamageFlags|=TakeDamageFlags_t.DFLAG_IGNORE_ARMOR|TakeDamageFlags_t.DFLAG_SUPPRESS_DAMAGE_MODIFICATION;
        }
        if(!connected||!mcReady||view==null||view.ReadInt32(28)==0)return HookResult.Continue;
        if(TryBlockC4Blast(hook,entity,info))return HookResult.Handled;
        var local=Utilities.GetPlayers().FirstOrDefault(p=>p.IsValid&&!p.IsBot&&p.PawnIsAlive&&p.PlayerPawn.Value?.Handle==entity.Handle);
        if(local?.PlayerPawn.Value is not {} pawn)return HookResult.Continue;
        // Interaction bots never damage the human; held bots also remain safe outside that scene.
        if(IsPeacefulActorAttacker(info.Attacker.Raw)||IsControlledActorAttacker(info.Attacker.Raw)){
            info.Damage=0;info.ShouldBleed=false;
            info.DamageFlags|=TakeDamageFlags_t.DFLAG_SUPPRESS_EFFECTS|TakeDamageFlags_t.DFLAG_SUPPRESS_PHYSICS_FORCE;
            hook.SetReturn(0);return HookResult.Handled;
        }
        var blockable=DamageTypes_t.DMG_BULLET|DamageTypes_t.DMG_BUCKSHOT|DamageTypes_t.DMG_SLASH|DamageTypes_t.DMG_CLUB;
        bool shield=shieldRaised&&Environment.TickCount64-shieldReceivedAt<=250&&(testing||(lastInputActive&&(view.ReadInt64(96)&2)!=0));
        var source=info.Attacker.Value?.AbsOrigin;var at=pawn.AbsOrigin;
        if(shield&&info.Damage>0&&(info.BitsDamageType&blockable)!=0&&source!=null&&at!=null){
            float yaw=pawn.V_angle.Y*MathF.PI/180f;
            if((source.X-at.X)*MathF.Cos(yaw)+(source.Y-at.Y)*MathF.Sin(yaw)>0){
                float blocked=info.Damage;info.Damage=0;info.ShouldBleed=false;
                info.DamageFlags|=TakeDamageFlags_t.DFLAG_SUPPRESS_EFFECTS|TakeDamageFlags_t.DFLAG_SUPPRESS_PHYSICS_FORCE;
                if(Environment.TickCount64-lastShieldSound>=120){lastShieldSound=Environment.TickCount64;Send(new{t="shieldhit"});}
                Log($"shield blocked damage={blocked} hp={pawn.Health} attacker={info.Attacker.Value?.Index}");
                hook.SetReturn(0);return HookResult.Handled;
            }
        }
        return ProtectWithMCConsumables(hook,info,local);
    }

    static V3 ReadCombatPoint(JsonElement m,string key,Vector fallback) {
        if(m.TryGetProperty(key,out var a)&&a.ValueKind==JsonValueKind.Array&&a.GetArrayLength()==3) {
            var p=new V3(a[0].GetSingle(),a[1].GetSingle(),a[2].GetSingle());
            if(float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z))return WorldGeometry.ToCS(p);
        }
        return new V3(fallback.X,fallback.Y,fallback.Z+40);
    }
    static DamageTypes_t NativeDamageType(string kind)=>kind switch {
        "arrow" or "trident"=>DamageTypes_t.DMG_BULLET,
        "player" or "mob" or "mace_smash"=>DamageTypes_t.DMG_SLASH,
        "explosion" or "player_explosion" or "fireworks"=>DamageTypes_t.DMG_BLAST,
        "onFire" or "inFire" or "lava" or "hotFloor"=>DamageTypes_t.DMG_BURN,
        "fall"=>DamageTypes_t.DMG_FALL,
        "fallingBlock" or "fallingAnvil"=>DamageTypes_t.DMG_CRUSH,
        _=>DamageTypes_t.DMG_GENERIC
    };
    void HurtNative(CCSPlayerController victim,int damage,string kind,CCSPlayerController? attacker,V3 from,V3 hit) {
        var pawn=victim.PlayerPawn.Value;if(pawn==null||!pawn.IsValid||!victim.PawnIsAlive)return;
        var hurt=Utilities.CreateEntityByName<CPointHurt>("point_hurt");if(hurt==null)return;
        var attackerPawn=attacker?.PlayerPawn.Value;
        string targetName=pawn.Entity!.Name;
        if(string.IsNullOrEmpty(targetName)) {
            // A pawn-lifetime name avoids restoring a null native string after every hit.
            targetName="mccs_damage_"+pawn.EntityHandle.Raw;
            pawn.Entity.Name=targetName;
        }
        try {
            hurt.StrTarget=targetName;hurt.Damage=damage;
            hurt.BitsDamageType=NativeDamageType(kind);hurt.Radius=0;hurt.Delay=1;
            hurt.Teleport(new Vector(from.X,from.Y,from.Z),null,null);hurt.DispatchSpawn();
            int before=pawn.Health;nativeDamageDepth++;scriptedAttacker=attacker;scriptedVictim=pawn;scriptedDamage=damage;
            try { hurt.AcceptInput("Hurt",attackerPawn,attackerPawn); } finally {nativeDamageDepth--;scriptedAttacker=null;scriptedVictim=null;}
            var direction=hit-from;if(direction.LengthSquared()>0.001f)direction=V3.Normalize(direction);
            if(pawn.IsValid && pawn.Health<before)EmitNativeBlood(hit,direction);
            Log($"MC native damage victim={victim.Index} kind={kind} before={before} after={pawn.Health}");
        } finally {AddTimer(.2f,()=>{if(hurt.IsValid)hurt.Remove();});}
    }
    void EmitNativeBlood(V3 hit,V3 direction) {
        if(bloodEffects.Count>=32)return;
        var fx=Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");if(fx==null)return;
        fx.EffectName=BloodEffect;fx.StartActive=false;
        fx.Teleport(new Vector(hit.X,hit.Y,hit.Z),new QAngle(0,MathF.Atan2(direction.Y,direction.X)*180/MathF.PI,0),null);
        fx.DispatchSpawn();fx.AcceptInput("Start");bloodEffects.Add(fx);
        AddTimer(2f,()=>{if(fx.IsValid)fx.Remove();bloodEffects.Remove(fx);});
    }
    void TestNativeDamage(CCSPlayerController local,JsonElement m) {
        var bot=Utilities.GetPlayers().FirstOrDefault(p=>p.IsValid&&p.IsBot&&p.PawnIsAlive);
        if(bot?.PlayerPawn.Value?.AbsOrigin is not {} at || local.PlayerPawn.Value?.AbsOrigin is not {} target)return;
        // Uses the same pre-damage hook as a real CS bullet; only available in explicit test mode.
        var victim=m.TryGetProperty("bot",out var b)&&b.GetBoolean()?bot:local;
        var attacker=victim==bot?local:bot;
        var from=attacker.PlayerPawn.Value!.AbsOrigin!;var hit=victim.PlayerPawn.Value!.AbsOrigin!;
        HurtNative(victim,Math.Clamp(m.GetProperty("d").GetInt32(),1,150),"arrow",attacker,new V3(from.X,from.Y,from.Z+48),new V3(hit.X,hit.Y,hit.Z+48));
    }
}
