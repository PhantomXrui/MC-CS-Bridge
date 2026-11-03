package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.Passthrough;
import dev.rehan.passthrough.WorldBridge;
import net.minecraft.core.BlockPos;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.block.BarrierBlock;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.material.Fluid;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** A bucket hitting host geometry should pour onto its face, not into an invisible cell.
 * Movement-only barrier filtering lives in EntityMixin's contextual iterator: changing the
 * cached BarrierBlock shape here would also change arrows and unrelated ordinary entities. */
@Mixin(BarrierBlock.class)
abstract class BarrierBlockMixin {
    @Inject(method = "canPlaceLiquid", at = @At("HEAD"), cancellable = true)
    private void passthrough$solidHostTerrain(LivingEntity entity, BlockGetter level, BlockPos pos,
            BlockState state, Fluid fluid, CallbackInfoReturnable<Boolean> cir) {
        if (Passthrough.active && WorldBridge.isHostSupport(state,pos)) cir.setReturnValue(false);
    }
}
