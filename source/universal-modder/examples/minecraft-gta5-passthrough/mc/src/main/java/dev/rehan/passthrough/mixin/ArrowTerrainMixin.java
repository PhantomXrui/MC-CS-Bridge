package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.HostTerrain;
import dev.rehan.passthrough.WorldBridge;
import net.minecraft.core.BlockPos;
import net.minecraft.world.entity.projectile.arrow.AbstractArrow;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.shapes.Shapes;
import net.minecraft.world.phys.shapes.VoxelShape;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;

@Mixin(AbstractArrow.class)
abstract class ArrowTerrainMixin {
    @Redirect(method="tick",at=@At(value="INVOKE",target="Lnet/minecraft/world/level/block/state/BlockState;getCollisionShape(Lnet/minecraft/world/level/BlockGetter;Lnet/minecraft/core/BlockPos;)Lnet/minecraft/world/phys/shapes/VoxelShape;"))
    private VoxelShape passthrough$skipCoarseEmbedding(BlockState state,BlockGetter level,BlockPos pos){
        return HostTerrain.enabled() && WorldBridge.isHostSupport(state,pos)
            ? Shapes.empty() : state.getCollisionShape(level,pos);
    }
    @Redirect(method="tick",at=@At(value="INVOKE",target="Lnet/minecraft/world/level/Level;clipIncludingBorder(Lnet/minecraft/world/level/ClipContext;)Lnet/minecraft/world/phys/BlockHitResult;"))
    private BlockHitResult passthrough$exactTerrain(Level level,ClipContext context){
        return HostTerrain.clip(context,level.clipIncludingBorder(context));
    }
}
