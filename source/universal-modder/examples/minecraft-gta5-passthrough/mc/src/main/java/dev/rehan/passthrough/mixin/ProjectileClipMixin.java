package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.HostTerrain;
import dev.rehan.passthrough.WorldBridge;
import net.minecraft.core.BlockPos;
import net.minecraft.world.entity.projectile.arrow.AbstractArrow;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.shapes.CollisionContext;
import net.minecraft.world.phys.shapes.EntityCollisionContext;
import net.minecraft.world.phys.shapes.Shapes;
import net.minecraft.world.phys.shapes.VoxelShape;
import org.spongepowered.asm.mixin.Final;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

@Mixin(ClipContext.class)
abstract class ProjectileClipMixin {
    @Shadow @Final private CollisionContext collisionContext;
    @Inject(method="getBlockShape",at=@At("HEAD"),cancellable=true)
    private void passthrough$ignoreHostVoxels(BlockState state,BlockGetter level,BlockPos pos,CallbackInfoReturnable<VoxelShape> cir){
        if(HostTerrain.enabled() && collisionContext instanceof EntityCollisionContext context
                && (context.getEntity() instanceof AbstractArrow
                    || context.getEntity() instanceof Player player && HostTerrain.movementEntity(player))
                && WorldBridge.isHostSupport(state,pos))
            cir.setReturnValue(Shapes.empty());
    }
}
