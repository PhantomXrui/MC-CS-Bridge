package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.HostTerrain;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.shapes.EntityCollisionContext;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Vanilla eyes/direction/range, with native CS surfaces replacing coarse support voxels. */
@Mixin(BlockGetter.class)
public interface NativePlayerRayMixin {
    @Inject(method="clip(Lnet/minecraft/world/level/ClipContext;)Lnet/minecraft/world/phys/BlockHitResult;",
            at=@At("RETURN"),cancellable=true)
    private void passthrough$exactPlayerRay(ClipContext context,CallbackInfoReturnable<BlockHitResult> cir) {
        var collision=((ClipContextAccessor)context).passthrough$rayCollisionContext();
        if(collision instanceof EntityCollisionContext owner && owner.getEntity() instanceof Player player
                && HostTerrain.movementEntity(player)) {
            // BlockGetter.clip covers Entity.pick and Item.getPlayerPOVHitResult (boats/buckets).
            // Merge the nearer true map hit; never discard an ordinary MC block/fluid hit.
            cir.setReturnValue(HostTerrain.clip(context,cir.getReturnValue()));
        }
    }
}
