package dev.rehan.passthrough;

import com.google.common.collect.Iterables;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicLong;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.animal.chicken.Chicken;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.level.BlockCollisions;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.level.CollisionGetter;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.Vec3;
import net.minecraft.world.phys.shapes.BooleanOp;
import net.minecraft.world.phys.shapes.CollisionContext;
import net.minecraft.world.phys.shapes.EntityCollisionContext;
import net.minecraft.world.phys.shapes.Shapes;
import net.minecraft.world.phys.shapes.VoxelShape;

/** Exact projectile rays plus bounded, conservative terrain shapes for vanilla MC movement. */
public final class HostTerrain {
    // Multiple scale-2 meme carriers otherwise evict one another's floor every tick,
    // repeatedly triggering the conservative movement hold. Keep bounded room for a group.
    private static final int CACHE_CELLS=1024, CACHE_SURFACES=131072, MAX_QUERY_CELLS=64, MAX_QUERY_SHAPES=4096, MAX_COLD_CELLS=16;
    private static final double QUERY_EPS=0.00001;
    private record Cell(int x,int y,int z) {}
    private record Surface(AABB box,VoxelShape shape) {}
    private static final class Terrain {
        final HostGeometry geometry;
        final String map;
        final Map<Cell,List<Surface>> cells=new LinkedHashMap<>(32,0.75f,true);
        final Map<Cell,String> rejected=new LinkedHashMap<>();
        int cachedSurfaces;
        Terrain(HostGeometry geometry,String map){this.geometry=geometry;this.map=map;}
        synchronized List<Surface> surfaces(Cell cell,int[] cold){
            var found=cells.get(cell);if(found!=null){cacheHits.incrementAndGet();return found;}
            String failure=rejected.get(cell);if(failure!=null)throw new HostGeometry.CollisionBudgetException(failure);
            if(cold[0]++>=MAX_COLD_CELLS)throw new HostGeometry.CollisionBudgetException("movement cache warming budget");
            var built=new ArrayList<Surface>();
            try{
                for(var b:geometry.movementBoxes(cell.x,cell.y,cell.z)){
                    var box=new AABB(b.minX(),b.minY(),b.minZ(),b.maxX(),b.maxY(),b.maxZ());
                    built.add(new Surface(box,Shapes.create(box)));
                }
            }catch(HostGeometry.CollisionBudgetException e){
                rejected.put(cell,e.getMessage());
                if(rejected.size()>CACHE_CELLS)rejected.remove(rejected.keySet().iterator().next());
                throw e;
            }
            var result=List.copyOf(built);cells.put(cell,result);cacheBuilds.incrementAndGet();
            cachedSurfaces+=result.size();
            while(cells.size()>CACHE_CELLS||cachedSurfaces>CACHE_SURFACES){
                var oldest=cells.entrySet().iterator().next();cachedSurfaces-=oldest.getValue().size();cells.remove(oldest.getKey());
            }
            return result;
        }
    }
    private static volatile Terrain terrain;
    private static Path cacheRoot;
    private static volatile String requestedMap="";
    private static long loadGeneration;
    private static final AtomicLong cacheHits=new AtomicLong(),cacheBuilds=new AtomicLong(),queries=new AtomicLong(),budgetHolds=new AtomicLong(),maxQueryNanos=new AtomicLong();
    private static volatile long lastWarningNanos;
    private static volatile int lastQueryShapes;
    private HostTerrain() {}

    public static void initialize(){
        String path=System.getProperty("passthrough.hostGeometry", "");
        if(path.isEmpty())return;
        try{
            Path file=Path.of(path).toAbsolutePath().normalize();
            cacheRoot=file.getParent().getParent();requestedMap=file.getParent().getFileName().toString();
            terrain=new Terrain(new HostGeometry(file),requestedMap);
            Passthrough.LOG.info("Host terrain {}: {} triangles",requestedMap,terrain.geometry.count());
        }catch(Exception e){terrain=null;Passthrough.LOG.error("Host geometry unavailable; retaining barrier fallback",e);}
    }
    /** Called for cam.map. Old geometry is never used while another map is loading. */
    public static synchronized void selectMap(String map){
        if(map==null||map.equals(requestedMap))return;
        long generation=++loadGeneration;requestedMap=map;terrain=null;
        if(cacheRoot==null||!map.matches("[a-z0-9_]{1,64}")){
            Passthrough.LOG.error("Host movement geometry has no valid cache for map {}",map);return;
        }
        Path file=cacheRoot.resolve(map).resolve("world.tri").normalize();
        if(!file.startsWith(cacheRoot)||!Files.isRegularFile(file)){
            Passthrough.LOG.error("Host movement geometry cache missing: {}",file);return;
        }
        Thread loader=new Thread(()->{
            try{
                Terrain loaded=new Terrain(new HostGeometry(file),map);
                synchronized(HostTerrain.class){if(generation!=loadGeneration)return;terrain=loaded;}
                Passthrough.LOG.info("Host terrain switched to {}: {} triangles",map,loaded.geometry.count());
            }catch(Exception e){Passthrough.LOG.error("Host terrain map load failed: {}",map,e);}
        },"mc-host-terrain-load");
        loader.setDaemon(true);loader.start();
    }
    public static boolean enabled(){return Passthrough.active&&terrain!=null;}
    /** The movement owner must wait for this before enabling input/anchoring on a changed map. */
    public static boolean movementReady(){return terrain!=null;}
    public static boolean movementEntity(Entity entity){
        if(entity==null||entity.noPhysics||!entity.level().dimension().equals(Level.OVERWORLD))return false;
        // Chickens continue ticking when the renderer/host heartbeat is inactive. Their floor
        // must be local geometry, not transient streamed barriers. Adults need the same support.
        if(MobWar.VANILLA_COMBAT&&entity instanceof Chicken&&cacheRoot!=null)return true;
        // Retain exact support after F8 ends or focus changes; coarse voxels can bury a relocated mob.
        if(MobWar.VANILLA_COMBAT&&entity instanceof net.minecraft.world.entity.Mob
                &&entity.entityTags().contains("passthrough_gathered")&&cacheRoot!=null)return true;
        if(!enabled())return false;
        if(entity instanceof net.minecraft.world.entity.Mob mob && DanceGather.controls(mob))return true;
        if(entity instanceof Player player)return !player.isSpectator()&&HostPlayerBody.minecraftMovement(player);
        if(!HostPlayerBody.minecraftMovement()||!entity.isVehicle())return false;
        for(Entity passenger:entity.getIndirectPassengers())
            if(passenger instanceof Player player&&!player.isSpectator()&&HostPlayerBody.minecraftMovement(player))return true;
        return false;
    }
    /** Same BlockCollisions iterator vanilla uses, retaining every ordinary MC shape unchanged. */
    public static Iterable<VoxelShape> movementCollisions(CollisionGetter level,CollisionContext context,AABB query){
        if(!(context instanceof EntityCollisionContext entityContext)||!movementEntity(entityContext.getEntity()))return null;
        Entity entity=entityContext.getEntity();
        Terrain current=terrain;
        // A map switch drops the old geometry before loading the new cache. Hold chickens
        // locally during that gap instead of using the wrong map or letting them fall.
        if(current==null)return holdSurfaces(entity.getBoundingBox());
        Iterable<VoxelShape> vanilla=()->new BlockCollisions<VoxelShape>(level,context,query,false,(pos,shape)->
            WorldBridge.isHostSupport(level.getBlockState(pos),pos)?Shapes.empty():shape);
        List<VoxelShape> host;
        long start=System.nanoTime();
        try{host=movementShapes(current,query);}
        catch(HostGeometry.CollisionBudgetException e){
            budgetHolds.incrementAndGet();host=holdSurfaces(entity.getBoundingBox());
            long now=System.nanoTime();
            if(now-lastWarningNanos>3_000_000_000L){lastWarningNanos=now;Passthrough.LOG.warn("Host terrain movement held at budget limit: {}",e.getMessage());}
        }
        long elapsed=System.nanoTime()-start;maxQueryNanos.accumulateAndGet(elapsed,Math::max);
        queries.incrementAndGet();lastQueryShapes=host.size();
        // Vanilla's server movement validator assumes every returned shape is nonempty:
        // an empty placeholder fails its overlap-with-old-body test and forces a teleport.
        // Filter the combined iterator so neither hidden supports nor host surfaces leak empties.
        return Iterables.filter(Iterables.concat(vanilla,host),shape->!shape.isEmpty());
    }
    private static List<VoxelShape> movementShapes(Terrain current,AABB query){
        if(!Double.isFinite(query.minX)||!Double.isFinite(query.minY)||!Double.isFinite(query.minZ)
                ||!Double.isFinite(query.maxX)||!Double.isFinite(query.maxY)||!Double.isFinite(query.maxZ))
            throw new HostGeometry.CollisionBudgetException("nonfinite movement bounds");
        if(Math.max(Math.max(Math.abs(query.minX),Math.abs(query.maxX)),Math.max(Math.abs(query.minZ),Math.abs(query.maxZ)))>30_000_000
                ||Math.max(Math.abs(query.minY),Math.abs(query.maxY))>30_000_000)
            throw new HostGeometry.CollisionBudgetException("movement outside coordinate bounds");
        int x0=(int)Math.floor(query.minX-QUERY_EPS),y0=(int)Math.floor(query.minY-QUERY_EPS),z0=(int)Math.floor(query.minZ-QUERY_EPS);
        int x1=(int)Math.floor(query.maxX+QUERY_EPS),y1=(int)Math.floor(query.maxY+QUERY_EPS),z1=(int)Math.floor(query.maxZ+QUERY_EPS);
        long count=((long)x1-x0+1)*((long)y1-y0+1)*((long)z1-z0+1);
        if(count<=0||count>MAX_QUERY_CELLS)throw new HostGeometry.CollisionBudgetException("movement sweep cell budget");
        var result=new ArrayList<VoxelShape>();var seen=new HashSet<AABB>();int[] cold={0};
        for(int x=x0;x<=x1;x++)for(int y=y0;y<=y1;y++)for(int z=z0;z<=z1;z++){
            for(Surface surface:current.surfaces(new Cell(x,y,z),cold)){
                if(!surface.box.intersects(query)||!seen.add(surface.box))continue;
                if(result.size()>=MAX_QUERY_SHAPES)throw new HostGeometry.CollisionBudgetException("movement sweep shape budget");
                result.add(surface.shape);
            }
        }
        return result;
    }
    /** A bounded local stop if geometry is too complex; never silently drop collision surfaces. */
    private static List<VoxelShape> holdSurfaces(AABB b){
        double e=0.00001;
        return List.of(Shapes.create(new AABB(b.minX-e,b.minY-1,b.minZ-1,b.minX,b.maxY+1,b.maxZ+1)),
            Shapes.create(new AABB(b.maxX,b.minY-1,b.minZ-1,b.maxX+e,b.maxY+1,b.maxZ+1)),
            Shapes.create(new AABB(b.minX-1,b.minY-e,b.minZ-1,b.maxX+1,b.minY,b.maxZ+1)),
            Shapes.create(new AABB(b.minX-1,b.maxY,b.minZ-1,b.maxX+1,b.maxY+e,b.maxZ+1)),
            Shapes.create(new AABB(b.minX-1,b.minY-1,b.minZ-e,b.maxX+1,b.maxY+1,b.minZ)),
            Shapes.create(new AABB(b.minX-1,b.minY-1,b.maxZ,b.maxX+1,b.maxY+1,b.maxZ+e)));
    }
    /** Coarse support voxels must not suffocate a human standing beside the actual thin CS wall. */
    public static Boolean suffocatesAt(Player player,BlockPos pos){
        if(!movementEntity(player))return null;
        AABB body=player.getBoundingBox();
        AABB column=new AABB(pos.getX(),body.minY,pos.getZ(),pos.getX()+1,body.maxY,pos.getZ()+1).deflate(1e-7);
        VoxelShape test=Shapes.create(column);
        return BlockPos.betweenClosedStream(column).anyMatch(p->{
            var state=player.level().getBlockState(p);
            return !WorldBridge.isHostSupport(state,p)&&!state.isAir()&&state.isSuffocating(player.level(),p)
                &&Shapes.joinIsNotEmpty(state.getCollisionShape(player.level(),p).move(p),test,BooleanOp.AND);
        });
    }

    public static Boolean insideWall(Entity entity){
        if(!movementEntity(entity))return null;
        double width=entity.getBbWidth()*0.8;
        AABB eye=AABB.ofSize(entity.getEyePosition(),width,1e-6,width);
        VoxelShape test=Shapes.create(eye);
        return BlockPos.betweenClosedStream(eye).anyMatch(pos->{
            var state=entity.level().getBlockState(pos);
            return !WorldBridge.isHostSupport(state,pos)&&!state.isAir()&&state.isSuffocating(entity.level(),pos)
                &&Shapes.joinIsNotEmpty(state.getCollisionShape(entity.level(),pos).move(pos),test,BooleanOp.AND);
        });
    }
    public static Map<String,Object> diagnostics(){
        Terrain current=terrain;
        return Map.of("ready",current!=null,"map",requestedMap,"triangles",current==null?0:current.geometry.count(),
            "queries",queries.get(),"cacheHits",cacheHits.get(),"cacheBuilds",cacheBuilds.get(),
            "budgetHolds",budgetHolds.get(),"lastShapes",lastQueryShapes,"maxQueryMs",maxQueryNanos.get()/1_000_000.0);
    }
    private static double[] cs(Vec3 p){return new double[]{p.x*40,-p.z*40,(p.y-96)*40};}
    public static Vec3 gatherFloor(Vec3 top,Vec3 bottom){
        // Geometry belongs to the loaded map and remains valid when the host loses focus.
        // Server-side courtship must not lose its floor when the player switches windows.
        Terrain current=terrain;if(current==null)return null;
        var hit=current.geometry.ray(cs(top),cs(bottom));
        return hit==null?null:new Vec3(hit.x()/40,hit.z()/40+96,-hit.y()/40);
    }
    public static BlockHitResult clip(ClipContext context,BlockHitResult vanilla){
        Terrain current=terrain;if(!Passthrough.active||current==null)return vanilla;
        var hit=current.geometry.ray(cs(context.getFrom()),cs(context.getTo()));if(hit==null)return vanilla;
        Vec3 at=new Vec3(hit.x()/40,hit.z()/40+96,-hit.y()/40);
        if(vanilla.getType()!=HitResult.Type.MISS&&context.getFrom().distanceToSqr(vanilla.getLocation())<=context.getFrom().distanceToSqr(at))return vanilla;
        Vec3 normal=new Vec3(hit.nx(),hit.nz(),-hit.ny());
        Direction side=Direction.getApproximateNearest(normal.x,normal.y,normal.z);
        return new BlockHitResult(at,side,BlockPos.containing(at.subtract(normal.scale(0.0001))),false);
    }
}
