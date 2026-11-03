package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.Passthrough;
import net.minecraft.world.entity.Mob;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Minecraft's world is always day here (so its lighting stays bright): undead don't burn in it. */
@Mixin(Mob.class)
abstract class MobMixin {
    @Inject(method="setTarget",at=@At("HEAD"),cancellable=true)
    private void passthrough$alliedTarget(net.minecraft.world.entity.LivingEntity target,CallbackInfo ci){
        if(dev.rehan.passthrough.MobWar.VANILLA_COMBAT && Passthrough.active && target!=null
            && dev.rehan.passthrough.MobWar.fighter((Mob)(Object)this) && !dev.rehan.passthrough.MobWar.isProxy(target))ci.cancel();
    }

	@Inject(method = "burnUndead()V", at = @At("HEAD"), cancellable = true)
	private void passthrough$noSunburn(final CallbackInfo ci) {
		if (Passthrough.active) {
			ci.cancel();
		}
	}
}
