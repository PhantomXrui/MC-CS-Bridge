package dev.rehan.passthrough;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Optional;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.level.CollisionGetter;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.Vec3;
import net.minecraft.world.phys.shapes.VoxelShape;

/** Bounded free-position probing; never form a voxel union of independent CS triangle surfaces. */
public final class HostFreePosition {
    private static final int MAX_BOXES=4096, MAX_CANDIDATES=160;
    private static final double EPS=1.0e-7;
    private HostFreePosition() {}

    public static Optional<Vec3> find(CollisionGetter level,Entity entity,VoxelShape allowed,Vec3 target,
            double width,double height,double depth) {
        if(allowed.isEmpty())return Optional.empty();
        if(!Double.isFinite(width)||!Double.isFinite(height)||!Double.isFinite(depth)
                ||width<0||height<0||depth<0)return Optional.empty();
        AABB bounds=allowed.bounds();
        var obstacles=new ArrayList<AABB>();
        var border=level.getWorldBorder();
        for(VoxelShape shape:level.getBlockCollisions(entity,bounds.inflate(width,height,depth))) {
            if(shape.isEmpty()||(border!=null&&!border.isWithinBounds(shape.bounds())))continue;
            for(AABB box:shape.toAabbs()) {
                // Inflate each obstacle individually, exactly as vanilla does before its union.
                AABB inflated=box.inflate(width/2,height/2,depth/2);
                if(!inflated.intersects(bounds))continue;
                if(obstacles.size()>=MAX_BOXES)return Optional.empty();
                obstacles.add(inflated);
            }
        }
        return search(allowed,target,obstacles);
    }

    /** Empty means no bounded, verified candidate; vanilla also permits the entity to retain its position. */
    public static Optional<Vec3> search(VoxelShape allowed,Vec3 target,List<AABB> obstacles) {
        if(allowed.isEmpty()||obstacles.size()>MAX_BOXES)return Optional.empty();
        AABB bounds=allowed.bounds();
        var candidates=new ArrayList<Vec3>();
        // Include the preferred point and the search-region faces/corners. In particular the upper
        // face lets a growing chick keep its feet on the floor when its new centre moves upwards.
        double cx=Math.clamp(target.x,bounds.minX,bounds.maxX);
        double cy=Math.clamp(target.y,bounds.minY,bounds.maxY);
        double cz=Math.clamp(target.z,bounds.minZ,bounds.maxZ);
        for(double x:new double[]{cx,bounds.minX,bounds.maxX})
            for(double y:new double[]{cy,bounds.minY,bounds.maxY})
                for(double z:new double[]{cz,bounds.minZ,bounds.maxZ})add(candidates,allowed,new Vec3(x,y,z));
        // Nearby individual surface faces find smaller displacements than the bounding-box corners.
        for(AABB box:obstacles) {
            if(candidates.size()+6>MAX_CANDIDATES)break;
            add(candidates,allowed,new Vec3(box.minX,cy,cz));
            add(candidates,allowed,new Vec3(box.maxX,cy,cz));
            add(candidates,allowed,new Vec3(cx,box.minY,cz));
            add(candidates,allowed,new Vec3(cx,box.maxY,cz));
            add(candidates,allowed,new Vec3(cx,cy,box.minZ));
            add(candidates,allowed,new Vec3(cx,cy,box.maxZ));
        }
        candidates.sort(Comparator.comparingDouble(target::distanceToSqr));
        for(Vec3 point:candidates) {
            boolean blocked=false;
            for(AABB box:obstacles) {
                if(point.x>box.minX+EPS&&point.x<box.maxX-EPS
                        &&point.y>box.minY+EPS&&point.y<box.maxY-EPS
                        &&point.z>box.minZ+EPS&&point.z<box.maxZ-EPS){blocked=true;break;}
            }
            if(!blocked)return Optional.of(point);
        }
        return Optional.empty();
    }

    private static void add(List<Vec3> candidates,VoxelShape allowed,Vec3 proposed) {
        if(candidates.size()>=MAX_CANDIDATES)return;
        // The allowed shape can have holes: project onto it rather than treating its bounds as solid.
        allowed.closestPointTo(proposed).ifPresent(point->{if(!candidates.contains(point))candidates.add(point);});
    }
}
