package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.HostTerrain;
import dev.rehan.passthrough.HostFreePosition;
import java.util.Optional;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.level.CollisionGetter;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.Vec3;
import net.minecraft.world.phys.shapes.CollisionContext;
import net.minecraft.world.phys.shapes.VoxelShape;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Vanilla move, step and sneak/noCollision probes share this contextual collision entry. */
@Mixin(CollisionGetter.class)
public interface EntityMixin {
    @Inject(method="findFreePosition",at=@At("HEAD"),cancellable=true)
    private void passthrough$boundedHostFreePosition(Entity entity,VoxelShape allowed,Vec3 target,
            double width,double height,double depth,CallbackInfoReturnable<Optional<Vec3>> cir){
        if(HostTerrain.movementEntity(entity))
            cir.setReturnValue(HostFreePosition.find((CollisionGetter)this,entity,allowed,target,width,height,depth));
    }

    @Inject(method="getBlockCollisionsFromContext",at=@At("HEAD"),cancellable=true)
    private void passthrough$hostMovementShapes(CollisionContext context,AABB box,
            CallbackInfoReturnable<Iterable<VoxelShape>> cir){
        Iterable<VoxelShape> collision=HostTerrain.movementCollisions((CollisionGetter)this,context,box);
        if(collision!=null)cir.setReturnValue(collision);
    }

    @Mixin(Entity.class)
    abstract class Body {
        @Inject(method="isInWall",at=@At("HEAD"),cancellable=true)
        private void passthrough$ignoreCoarseSuffocation(CallbackInfoReturnable<Boolean> cir){
            Boolean inside=HostTerrain.insideWall((Entity)(Object)this);
            if(inside!=null)cir.setReturnValue(inside);
        }
    }
}
