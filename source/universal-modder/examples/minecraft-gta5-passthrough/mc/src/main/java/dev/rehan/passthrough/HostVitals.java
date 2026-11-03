package dev.rehan.passthrough;

import com.google.gson.JsonObject;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicReference;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.effect.MobEffects;
import net.minecraft.world.entity.LivingEntity;

/** CS owns life/health; Minecraft owns food and consumable effects. Never echo host corrections as healing. */
public final class HostVitals {
    private record Snapshot(long life,float health,float max,float absorption,boolean alive,long at) {}
    private static final AtomicReference<Snapshot> pending=new AtomicReference<>();
    private static volatile Snapshot latest;
    private static long life,sequence;
    private static boolean applying;
    private static double lastX,lastZ;
    private static boolean havePosition;
    public static boolean enabled(){var s=latest;return MobWar.VANILLA_COMBAT && Passthrough.active && s!=null && System.nanoTime()-s.at<2_000_000_000L;}
    public static void receive(JsonObject m){
        float hp=m.get("health").getAsFloat(),max=m.get("max").getAsFloat(),abs=m.get("absorption").getAsFloat();
        if(!Float.isFinite(hp)||!Float.isFinite(max)||!Float.isFinite(abs)||max<=0)return;
        var s=new Snapshot(m.get("life").getAsLong(),hp,max,Math.clamp(abs,0,40),m.get("alive").getAsBoolean(),System.nanoTime());
        latest=s;pending.set(s);
    }
    public static void beginTick(MinecraftServer server){
        Snapshot s=pending.getAndSet(null);if(s==null||!enabled())return;
        for(ServerPlayer p:server.getPlayerList().getPlayers()){
            // A health correction must not resurrect the old entity after vanilla death.
            // In 26.3 that leaves waitingForRespawn set, discarding movement/use packets,
            // while the positive health also makes the normal respawn request be rejected.
            if(p.isDeadOrDying() || !p.connection.hasClientLoaded())continue;
            applying=true;
            try{
                if(life!=s.life){
                    if(life!=0){p.removeAllEffects();p.getFoodData().setFoodLevel(20);p.getFoodData().setSaturation(5);}
                    life=s.life;sequence=0;havePosition=false;
                }
                p.setHealth(Math.clamp(s.health/s.max*p.getMaxHealth(),0.1f,p.getMaxHealth()));
                p.setAbsorptionAmount(s.absorption);
            }finally{applying=false;}
        }
    }
    public static void endTick(MinecraftServer server){
        if(!enabled()){havePosition=false;return;}
        for(ServerPlayer p:server.getPlayerList().getPlayers()){
            // CS moves the player by teleport; account for real horizontal travel, excluding teleports.
            double dx=p.getX()-lastX,dz=p.getZ()-lastZ,dist=Math.sqrt(dx*dx+dz*dz);
            if(havePosition && dist<1.5 && latest.alive)p.getFoodData().addExhaustion((float)(dist*0.1));
            lastX=p.getX();lastZ=p.getZ();havePosition=true;
            var resistance=p.getEffect(MobEffects.RESISTANCE);
            Passthrough.events.accept(String.format(Locale.ROOT,"{\"t\":\"vitalsfx\",\"life\":%d,\"resistance\":%d,\"fireproof\":%b}",life,resistance==null?-1:resistance.getAmplifier(),p.hasEffect(MobEffects.FIRE_RESISTANCE)));
        }
    }
    public static boolean heal(LivingEntity entity,float amount){
        if(!(entity instanceof ServerPlayer p)||applying||!enabled()||life==0)return false;
        if(latest.alive && Float.isFinite(amount) && amount>0){
            float actual=Math.min(amount,p.getMaxHealth()-p.getHealth());
            if(actual>0)event("playerheal",actual);
        }
        return true; // Wait for the authoritative CS acknowledgement rather than optimistic HP.
    }
    public static void absorption(LivingEntity entity,float before,float after){
        if(!(entity instanceof ServerPlayer)||applying||!enabled()||life==0||!latest.alive)return;
        float delta=after-before;if(Float.isFinite(delta)&&Math.abs(delta)>.0001)event("playerabsorption",delta);
    }
    private static void event(String type,float amount){Passthrough.events.accept(String.format(Locale.ROOT,"{\"t\":\"%s\",\"life\":%d,\"seq\":%d,\"amount\":%.6f}",type,life,++sequence,amount));}
}
