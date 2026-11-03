package dev.rehan.passthrough;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import java.util.*;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.npc.villager.Villager;
import net.minecraft.world.phys.*;

/** Server-owned, shared crowd for both real mobs and invisible CS actor proxies. */
public final class DanceGather {
    private DanceGather() {}
    private static volatile long epoch, heartbeat, gatherRequest;
    private static long handledGather;
    private static long runningEpoch;
    private static final Map<Integer, Member> members = new LinkedHashMap<>();
    private static Random random = new Random();
    private static Vec3 anchor;
    private static volatile JsonObject snapshot = new JsonObject();
    private static final class Member {
        final Mob mob;
        final boolean noAI;
        final double radius;
        Vec3 goal;
        int attempts;
        long retry;
        boolean relocating;
        Member(Mob m) {
            mob=m; noAI=m.isNoAi(); goal=m.position();
            // Include the entire spinning body/held gun, and the peak 1.55x squash width.
            radius=MobWar.isProxy(m)?1.45*MobWar.scale(MobWar.handle(m)):
                Math.max(.65,m.getBbWidth()*1.1+.3);
        }
    }
    public static void receive(boolean on,long start,long request) {
        gatherRequest=on?request:0; epoch=on?start:0; heartbeat=System.currentTimeMillis();
    }
    public static boolean controls(Mob m) { return members.containsKey(m.getId()); }
    public static JsonObject inspect() { return snapshot.deepCopy(); }
    public static void clear() {
        for(Member m:members.values()) release(m);
        members.clear(); anchor=null; runningEpoch=0;
    }
    private static void release(Member m) {
        m.mob.setNoAi(m.noAI); m.mob.getNavigation().stop(); m.mob.setDeltaMovement(Vec3.ZERO);
        if(m.mob instanceof Villager v && MobWar.isProxy(v)) ActorInteractions.sendPose(MobWar.handle(v),v,"release");
    }
    private static double horizontal(Vec3 a,Vec3 b) { double x=a.x-b.x,z=a.z-b.z; return Math.sqrt(x*x+z*z); }
    private static boolean eligible(Mob m,ServerPlayer player) {
        return m.isAlive() && !m.isRemoved() && !m.isPassenger() && !m.isVehicle() && !m.isLeashed()
            && (!MobWar.isProxy(m) ||
                MobWar.team(MobWar.handle(m))==2 && !ActorInteractions.controls(MobWar.handle(m)));
    }
    static void tick(ServerLevel level) {
        if(epoch==0 || System.currentTimeMillis()-heartbeat>4000 || !Passthrough.active) { clear(); publish(); return; }
        ServerPlayer player=level.players().stream().filter(p->p.isAlive()&&!p.isSpectator()).findFirst().orElse(null);
        if(player==null) { clear(); return; }
        if(runningEpoch!=epoch) { clear(); runningEpoch=epoch; random=new Random(epoch); }
        var iterator=members.values().iterator();
        while(iterator.hasNext()) { var m=iterator.next(); if(!eligible(m.mob,player)) { release(m); iterator.remove(); } }
        long tick=level.getGameTime();
        if(tick%10==0 || members.isEmpty()) for(var e:level.getAllEntities())
            if(e instanceof Mob mob && eligible(mob,player) && !members.containsKey(mob.getId())) {
                Member m=new Member(mob); members.put(mob.getId(),m);
                mob.setNoAi(true); mob.setTarget(null); mob.getNavigation().stop(); mob.setDeltaMovement(Vec3.ZERO);
            }
        // Only an explicit command can relocate existing participants. F8 and host heartbeats
        // hold their current positions; walking near/past them never changes these slots.
        if(gatherRequest!=0 && gatherRequest!=handledGather) {
            handledGather=gatherRequest; anchor=player.position();
            for(Member m:members.values()) {m.goal=null;m.relocating=true;m.attempts=0;m.retry=tick;}
        }
        int slotBudget=1;
        for(Member m:members.values()) {
            Mob mob=m.mob;
            // Host snapshots remain authoritative: a rejected arrival must choose another slot.
            if(m.relocating && m.goal!=null && MobWar.isProxy(mob)) {
                if(mob.position().distanceToSqr(m.goal)<.01)m.relocating=false;
                else if(tick>m.retry) {m.goal=null;m.retry=tick;}
            }
            boolean physics=mob.noPhysics; mob.noPhysics=false;
            try {
                if(m.relocating && m.goal==null && tick>=m.retry && slotBudget>0) {
                    slotBudget--; m.retry=tick+10; assign(level,m);
                }
                boolean arrived=m.goal!=null;
                if(arrived) {
                    if(!MobWar.isProxy(mob))mob.addTag("passthrough_gathered");
                    mob.resetFallDistance();mob.setPos(m.goal);
                }
                mob.setDeltaMovement(Vec3.ZERO);
                if(mob instanceof Villager v && MobWar.isProxy(v))
                    ActorInteractions.sendPose(MobWar.handle(v),v,arrived&&m.relocating?"dance_arrive":"dance");
                else if(arrived)m.relocating=false;
            } finally { mob.noPhysics=physics; }
        }
        if(tick%10==0) publish();
    }
    private static Vec3 floor(ServerLevel level,Mob mob,Vec3 top,double depth) {
        Vec3 bottom=top.add(0,-depth,0);
        Vec3 found=HostTerrain.gatherFloor(top,bottom);
        for(int y=(int)Math.floor(top.y);y>=(int)Math.floor(bottom.y);y--) {
            BlockPos pos=BlockPos.containing(top.x,y,top.z);
            var state=level.getBlockState(pos);
            if(WorldBridge.isHostSupport(state,pos))continue;
            var hit=state.getCollisionShape(level,pos,net.minecraft.world.phys.shapes.CollisionContext.of(mob)).clip(top,bottom,pos);
            if(hit!=null && (found==null || hit.getLocation().y>found.y))found=hit.getLocation();
        }
        return found==null?null:found.add(0,.005,0);
    }

    private static void assign(ServerLevel level,Member member) {
        // Random Poisson-disc samples, with larger rings only when the inner area fills.
        double inner=2.5+member.radius;
        for(int i=0;i<10;i++) {
            member.attempts++;
            double outer=Math.min(30,inner+3+member.attempts/18.0);
            if(inner>=outer)return;
            double angle=random.nextDouble()*Math.PI*2;
            double distance=Math.sqrt(inner*inner+random.nextDouble()*(outer*outer-inner*inner));
            Vec3 point=anchor.add(Math.cos(angle)*distance,2,Math.sin(angle)*distance);
            boolean blocked=false;
            for(Member other:members.values()) if(other!=member &&
                    horizontal(point,other.goal!=null?other.goal:other.mob.position())<member.radius+other.radius+.4) {blocked=true;break;}
            if(blocked)continue;
            Vec3 ground=floor(level,member.mob,point,6);
            if(ground==null || Math.abs(ground.y-anchor.y)>2)continue;
            // Floor corners account for ramps without expanding the expensive movement voxel cache.
            double highest=ground.y;
            for(double dx:new double[]{-member.radius,0,member.radius})for(double dz:new double[]{-member.radius,0,member.radius}) {
                Vec3 sample=floor(level,member.mob,point.add(dx,0,dz),6);
                if(sample==null || Math.abs(sample.y-ground.y)>.65) { blocked=true;break; }
                highest=Math.max(highest,sample.y);
            }
            if(blocked)continue;
            ground=new Vec3(ground.x,highest,ground.z);
            if(MobWar.isProxy(member.mob)) {
                Vec3 nativeFloor=HostTerrain.gatherFloor(point,point.add(0,-6,0));
                if(nativeFloor==null || Math.abs(ground.y-nativeFloor.y)>.3)continue;
            }
            // A spinning model needs more space than its normal collision capsule.
            var wide=new AABB(ground.x-member.radius,ground.y+.02,ground.z-member.radius,
                ground.x+member.radius,ground.y+member.mob.getBbHeight(),ground.z+member.radius);
            for(BlockPos pos:BlockPos.betweenClosed(wide)) {
                var state=level.getBlockState(pos);
                if(WorldBridge.isHostSupport(state,pos))continue;
                for(AABB block:state.getCollisionShape(level,pos,net.minecraft.world.phys.shapes.CollisionContext.of(member.mob)).toAabbs())
                    if(block.move(pos).intersects(wide)) {blocked=true;break;}
                if(blocked)break;
            }
            // Cross-section rays test the original triangles directly; no cold movement-cell allocation.
            for(double y=wide.minY+.04;!blocked&&y<=wide.maxY;y+=.35)
                for(double d=-member.radius;d<=member.radius;d+=.35) {
                    if(HostTerrain.gatherFloor(new Vec3(wide.minX,y,ground.z+d),new Vec3(wide.maxX,y,ground.z+d))!=null
                        ||HostTerrain.gatherFloor(new Vec3(ground.x+d,y,wide.minZ),new Vec3(ground.x+d,y,wide.maxZ))!=null) {blocked=true;break;}
                }
            if(blocked)continue;
            member.goal=ground; member.retry=level.getGameTime()+20; return;
        }
    }
    private static void publish() {
        var result=new JsonObject(); result.addProperty("active",runningEpoch!=0);result.addProperty("mode","stationary_command_assembly");
        result.addProperty("gatherRequest",gatherRequest);result.addProperty("handledGather",handledGather);
        var rows=new JsonArray();
        for(Member m:members.values()) {
            var row=new JsonObject(); row.addProperty("id",m.mob.getId());row.addProperty("proxy",MobWar.isProxy(m.mob));
            row.addProperty("host",MobWar.isProxy(m.mob)?MobWar.handle(m.mob):-1);
            row.addProperty("kind",net.minecraft.core.registries.BuiltInRegistries.ENTITY_TYPE.getKey(m.mob.getType()).toString());
            row.addProperty("health",m.mob.getHealth());
            row.addProperty("attempts",m.attempts);row.addProperty("radius",m.radius); row.addProperty("assigned",m.goal!=null);
            row.addProperty("relocating",m.relocating);
            row.addProperty("noAI",m.mob.isNoAi());row.addProperty("originalNoAI",m.noAI);
            row.addProperty("remaining",m.goal==null?-1:horizontal(m.mob.position(),m.goal));
            var p=new JsonArray();p.add(m.mob.getX());p.add(m.mob.getY());p.add(m.mob.getZ());row.add("p",p);
            rows.add(row);
        }
        result.add("members",rows); snapshot=result;
    }
}
