package dev.rehan.passthrough;

import java.util.*;
import net.fabricmc.fabric.api.event.player.UseEntityCallback;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerLifecycleEvents;
import net.minecraft.core.particles.ParticleTypes;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.entity.animal.cow.Cow;
import net.minecraft.world.item.Items;
import net.minecraft.world.phys.Vec3;

/** Paired courtship for the tagged meme creatures only; ordinary cows and CS agents are untouched. */
public final class MemeBreeding {
    private static boolean initialized;
    private static final Map<Cow,State> fed=new LinkedHashMap<>();
    private static final class State {
        long until;
        Cow mate;
        int ticks,near;
        boolean previousNoAi;
        State(long deadline) { until=deadline; }
    }
    public static synchronized void initialize() {
        if(initialized)return;
        initialized=true;
        UseEntityCallback.EVENT.register((player,level,hand,entity,hit)->{
            if(!(entity instanceof Cow cow) || !cow.entityTags().contains("mccs_meme_carrier"))return InteractionResult.PASS;
            var stack=player.getItemInHand(hand);
            if(!stack.is(Items.CHICKEN))return InteractionResult.PASS;
            if(!(player instanceof ServerPlayer))return InteractionResult.SUCCESS;
            if(player.isSpectator() || !cow.isAlive() || cow.getAge()!=0 || fed.containsKey(cow))return InteractionResult.CONSUME;
            fed.put(cow,new State(level.getGameTime()+600));
            stack.consume(1,player);
            hearts(cow);
            Passthrough.LOG.info("meme breeding fed id={} uuid={}",cow.getId(),cow.getUUID());
            return InteractionResult.SUCCESS_SERVER;
        });
        ServerTickEvents.END_SERVER_TICK.register(server->tick());
        ServerLifecycleEvents.SERVER_STOPPING.register(server->{for(var cow:new ArrayList<>(fed.keySet()))release(cow);});
        Passthrough.LOG.info("meme breeding initialized: raw chicken / paired hearts / half-size child");
    }
    private static void release(Cow cow) {
        State s=fed.remove(cow);
        if(s==null)return;
        if(s.mate!=null) {
            cow.setNoAi(s.previousNoAi);cow.setDeltaMovement(Vec3.ZERO);
            Cow mate=s.mate;s.mate=null;
            State other=fed.get(mate);
            if(other!=null && other.mate==cow) {other.mate=null;mate.setNoAi(other.previousNoAi);mate.setDeltaMovement(Vec3.ZERO);fed.remove(mate);}
        }
    }
    private static boolean valid(Cow cow,State s) {
        return cow.isAlive()&&!cow.isRemoved()&&cow.getAge()==0&&cow.level().getGameTime()<s.until;
    }
    private static void tick() {
        for(var cow:new ArrayList<>(fed.keySet())) {
            State s=fed.get(cow);if(s==null)continue;
            if(!valid(cow,s)){release(cow);continue;}
            if(s.mate==null) {
                Cow best=null;double distance=64;
                for(var entry:fed.entrySet()) {
                    Cow other=entry.getKey();State os=entry.getValue();
                    if(other==cow||other.level()!=cow.level()||os.mate!=null||!valid(other,os))continue;
                    double d=cow.distanceToSqr(other);if(d<distance){best=other;distance=d;}
                }
                if(best!=null) {
                    State b=fed.get(best);s.mate=best;b.mate=cow;
                    s.previousNoAi=cow.isNoAi();b.previousNoAi=best.isNoAi();
                    cow.setNoAi(true);best.setNoAi(true);cow.getNavigation().stop();best.getNavigation().stop();
                    Passthrough.LOG.info("meme breeding paired {} {} distance={}",cow.getId(),best.getId(),Math.sqrt(distance));
                }
            }
            Cow mate=s.mate;
            if(mate==null || cow.getId()>mate.getId())continue;
            State b=fed.get(mate);
            if(b==null||b.mate!=cow||!valid(mate,b)||cow.distanceToSqr(mate)>144){release(cow);continue;}
            s.ticks++;
            double touch=(cow.getBbWidth()+mate.getBbWidth())*.5+.12;
            approach(cow,mate,touch);approach(mate,cow,touch);
            double distance=cow.distanceToSqr(mate);
            s.near=distance<=(touch+.25)*(touch+.25)?s.near+1:0;
            if(s.ticks%10==0){hearts(cow);hearts(mate);}
            if(s.ticks>=60&&s.near>=40) {
                var center=cow.position().add(mate.position()).scale(.5);
                var between=mate.position().subtract(cow.position());
                double len=Math.max(.001,Math.hypot(between.x,between.z));
                var desired=center.add(-between.z/len*1.3,0,between.x/len*1.3);
                var floor=HostTerrain.gatherFloor(desired.add(0,1.5,0),desired.add(0,-2,0));
                if(floor==null){release(cow);continue;}
                // Queue one atomic spawn function only after both parents have completed courtship.
                cow.setAge(1200);mate.setAge(1200);
                WorldBridge.command(String.format(Locale.ROOT,"execute in minecraft:overworld positioned %.5f %.5f %.5f summon minecraft:cow run function mccs_meme:child",floor.x,floor.y+.02,floor.z));
                Passthrough.LOG.info("meme breeding birth parents={} {} nearTicks={} distance={} childAt={}",cow.getId(),mate.getId(),s.near,Math.sqrt(distance),floor);
                release(cow);
            } else if(s.ticks>=300)release(cow);
        }
    }
    private static void approach(Cow cow,Cow mate,double touch) {
        var delta=mate.position().subtract(cow.position());double distance=Math.hypot(delta.x,delta.z);
        double speed=distance>touch?Math.min(.055,(distance-touch)*.5):0;
        cow.setDeltaMovement(Vec3.ZERO);
        if(speed==0)return;
        // The scale-2 carrier may overlap conservative host voxel surfaces on stairs.
        // Use short exact host rays for courtship, while retaining real placed-block collision.
        var start=cow.position();
        var desired=start.add(delta.x/distance*speed,0,delta.z/distance*speed);
        var floor=HostTerrain.gatherFloor(desired.add(0,.65,0),desired.add(0,-.75,0));
        if(floor==null||Math.abs(floor.y-start.y)>.6)return;
        var next=new Vec3(desired.x,floor.y+.002,desired.z);
        double radius=cow.getBbWidth()*.45;
        for(double side:new double[]{-radius,0,radius})for(double height:new double[]{.7,1.5,2.5}) {
            var offset=new Vec3(-delta.z/distance*side,height,delta.x/distance*side);
            var a=start.add(offset);var end=next.add(offset);var hit=HostTerrain.gatherFloor(a,end);
            if(hit!=null&&a.distanceToSqr(hit)<a.distanceToSqr(end)-1e-7)return;
        }
        var movement=next.subtract(start);var box=cow.getBoundingBox();
        var shapes=new ArrayList<net.minecraft.world.phys.shapes.VoxelShape>();
        for(var pos:net.minecraft.core.BlockPos.betweenClosed(box.expandTowards(movement).inflate(.001))) {
            var block=cow.level().getBlockState(pos);
            if(WorldBridge.isHostSupport(block,pos))continue;
            var shape=block.getCollisionShape(cow.level(),pos,net.minecraft.world.phys.shapes.CollisionContext.of(cow));
            if(!shape.isEmpty())shapes.add(shape.move(pos));
        }
        double y=net.minecraft.world.phys.shapes.Shapes.collide(net.minecraft.core.Direction.Axis.Y,box,shapes,movement.y);
        box=box.move(0,y,0);
        double x=net.minecraft.world.phys.shapes.Shapes.collide(net.minecraft.core.Direction.Axis.X,box,shapes,movement.x);
        box=box.move(x,0,0);
        double z=net.minecraft.world.phys.shapes.Shapes.collide(net.minecraft.core.Direction.Axis.Z,box,shapes,movement.z);
        cow.setPos(start.add(x,y,z));
        cow.setYRot((float)Math.toDegrees(Math.atan2(-delta.x,delta.z)));
    }
    private static void hearts(Cow cow) {
        if(cow.level() instanceof ServerLevel level)level.sendParticles(ParticleTypes.HEART,cow.getX(),cow.getY()+1.8,cow.getZ(),4,.45,.25,.45,0);
    }
}
