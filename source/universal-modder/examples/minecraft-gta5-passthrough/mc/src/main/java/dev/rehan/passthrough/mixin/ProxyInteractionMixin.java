package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.MobWar;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.entity.npc.villager.Villager;
import net.minecraft.world.entity.player.Player;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Right-clicking a CS target must use the held item, never open villager trading. */
@Mixin(Villager.class)
abstract class ProxyInteractionMixin {
    @Inject(method = "getDefaultDimensions", at = @At("HEAD"), cancellable = true)
    private void passthrough$hostBabyBounds(net.minecraft.world.entity.Pose pose,
            CallbackInfoReturnable<net.minecraft.world.entity.EntityDimensions> cir) {
        Villager self = (Villager)(Object)this;
        // 26.3 villagers have a special 0.49 x 0.98 baby body, not an adult body / 2.
        // Host children need the exact same uniform scale as their native agent model.
        if (MobWar.VANILLA_COMBAT && MobWar.isProxy(self))
            cir.setReturnValue(self.getType().getDimensions().scale(self.getAgeScale()));
    }

    @Inject(method = "mobInteract", at = @At("HEAD"), cancellable = true)
    private void passthrough$noTrading(Player player, InteractionHand hand, CallbackInfoReturnable<InteractionResult> cir) {
        if (MobWar.VANILLA_COMBAT && MobWar.isProxy((Villager)(Object)this)) cir.setReturnValue(InteractionResult.PASS);
    }
}
