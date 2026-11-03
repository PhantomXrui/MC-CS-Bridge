using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
namespace MinecraftBridge;
public sealed partial class MinecraftBridgePlugin {
    long vitalsLife,lastVitalsEvent,lastVitalsFx,lastAppleBlockLog;
    double healingRemainder;
    float playerAbsorption;
    int resistanceLevel=-1;
    bool playerFireproof;
    long EnsureVitals(CCSPlayerController player){
        long life=ActorLife(player);
        if(life!=vitalsLife){vitalsLife=life;lastVitalsEvent=0;healingRemainder=0;playerAbsorption=0;resistanceLevel=-1;playerFireproof=false;lastVitalsFx=0;}
        return life;
    }
    void SendPlayerVitals(CCSPlayerController player,CCSPlayerPawn pawn){
        long life=EnsureVitals(player);
        Send(new {t="vitals",life,health=player.PawnIsAlive?Math.Clamp(pawn.Health,0,100):0,max=100,absorption=playerAbsorption/5f,alive=player.PawnIsAlive,ack=lastVitalsEvent});
    }
    void ApplyPlayerVitals(JsonElement m,CCSPlayerController player,CCSPlayerPawn pawn,string type){
        if(!player.PawnIsAlive||m.GetProperty("life").GetInt64()!=EnsureVitals(player))return;
        if(type=="vitalsfx"){
            resistanceLevel=Math.Clamp(m.GetProperty("resistance").GetInt32(),-1,4);
            playerFireproof=m.GetProperty("fireproof").GetBoolean();lastVitalsFx=Environment.TickCount64;return;
        }
        long seq=m.GetProperty("seq").GetInt64();if(seq<=lastVitalsEvent)return;
        double amount=m.GetProperty("amount").GetDouble();if(!double.IsFinite(amount)||Math.Abs(amount)>40)return;
        lastVitalsEvent=seq;
        if(type=="playerheal"&&amount>0){
            double scaled=amount*5+healingRemainder;int heal=(int)Math.Floor(scaled);healingRemainder=scaled-heal;
            int before=pawn.Health;pawn.Health=Math.Min(100,pawn.Health+heal);Utilities.SetStateChanged(pawn,"CBaseEntity","m_iHealth");
            Log($"MC heal life={vitalsLife} before={before} after={pawn.Health} amount={amount}");
        }else if(type=="playerabsorption")playerAbsorption=Math.Clamp(playerAbsorption+(float)amount*5,0,200);
        SendPlayerVitals(player,pawn);
    }
    HookResult ProtectWithMCConsumables(DynamicHook hook,CTakeDamageInfo info,CCSPlayerController player){
        EnsureVitals(player);
        if(lastVitalsFx==0||Environment.TickCount64-lastVitalsFx>1500||info.Damage<=0)return HookResult.Continue;
        float original=info.Damage,damage=original;
        // Local gameplay rule: gold hearts grant bullet immunity until absorption expires.
        // Other damage still uses the normal resistance/absorption path below.
        if(playerAbsorption>0 && (info.BitsDamageType&(DamageTypes_t.DMG_BULLET|DamageTypes_t.DMG_BUCKSHOT))!=0){
            info.Damage=0;info.ShouldBleed=false;
            info.DamageFlags|=TakeDamageFlags_t.DFLAG_SUPPRESS_EFFECTS|TakeDamageFlags_t.DFLAG_SUPPRESS_PHYSICS_FORCE;
            long now=Environment.TickCount64;
            if(now-lastAppleBlockLog>=500){lastAppleBlockLog=now;Log($"MC apple bullet immunity blocked={original} buffer={playerAbsorption}");}
            hook.SetReturn(0);return HookResult.Handled;
        }
        if(playerFireproof && (info.BitsDamageType&DamageTypes_t.DMG_BURN)!=0)damage=0;
        if(resistanceLevel>=0)damage*=Math.Max(0,1-.2f*(resistanceLevel+1));
        float absorbed=Math.Min(playerAbsorption,damage);playerAbsorption-=absorbed;damage-=absorbed;info.Damage=damage;
        if(absorbed>0||damage!=original)Log($"MC protection damage={original} remaining={damage} absorbed={absorbed} buffer={playerAbsorption}");
        if(damage>0)return HookResult.Continue;
        info.ShouldBleed=false;info.DamageFlags|=TakeDamageFlags_t.DFLAG_SUPPRESS_EFFECTS;hook.SetReturn(0);return HookResult.Handled;
    }
}
