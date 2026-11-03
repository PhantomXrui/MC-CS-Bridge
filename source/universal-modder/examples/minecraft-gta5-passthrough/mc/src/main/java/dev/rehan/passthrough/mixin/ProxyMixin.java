package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.MobWar;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.LivingEntity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Unique;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * Host people's proxies: hits on them go to the host instead of doing damage; they neither push nor get pushed
 * (they follow their person every tick); and Steve's crosshair ignores them (no trading screens, no blocked clicks).
 */
@Mixin(LivingEntity.class)
abstract class ProxyMixin {
	@Unique private float passthrough$healthBefore;
	@Inject(method = "hurtServer(Lnet/minecraft/server/level/ServerLevel;Lnet/minecraft/world/damagesource/DamageSource;F)Z", at = @At("HEAD"), cancellable = true)
	private void passthrough$proxyHurt(final ServerLevel level, final DamageSource source, final float amount, final CallbackInfoReturnable<Boolean> cir) {
		LivingEntity self = (LivingEntity) (Object) this;
		if (!MobWar.VANILLA_COMBAT && MobWar.isProxy(self)) {
			MobWar.onProxyHit(self, source, amount);
			cir.setReturnValue(false);
		}
	}

	@Inject(method = "actuallyHurt", at = @At("HEAD"))
	private void passthrough$beforeDamage(ServerLevel level, DamageSource source, float amount, CallbackInfo ci) {
		LivingEntity self = (LivingEntity)(Object)this;
		if (MobWar.VANILLA_COMBAT && MobWar.isProxy(self)) passthrough$healthBefore = self.getHealth();
	}

	@Inject(method = "actuallyHurt", at = @At("RETURN"))
	private void passthrough$resolvedDamage(ServerLevel level, DamageSource source, float amount, CallbackInfo ci) {
		LivingEntity self = (LivingEntity)(Object)this;
		if (MobWar.VANILLA_COMBAT && MobWar.isProxy(self)) {
			float damage = Math.max(0, passthrough$healthBefore - self.getHealth());
			// Keep the stand-in alive; CS owns death. Preserve vanilla hurt timers and status effects.
			self.setHealth(passthrough$healthBefore);
			MobWar.onProxyDamage(self, source, damage);
		}
	}

	@Inject(method = "isPushable()Z", at = @At("HEAD"), cancellable = true)
	private void passthrough$proxyNotPushable(final CallbackInfoReturnable<Boolean> cir) {
		if (MobWar.isProxy((LivingEntity) (Object) this)) {
			cir.setReturnValue(false);
		}
	}

	@Inject(method = "pushEntities()V", at = @At("HEAD"), cancellable = true)
	private void passthrough$proxyNoPush(final CallbackInfo ci) {
		if (MobWar.isProxy((LivingEntity) (Object) this)) {
			ci.cancel();
		}
	}

	@Inject(method = "isPickable()Z", at = @At("HEAD"), cancellable = true)
	private void passthrough$proxyNotPickable(final CallbackInfoReturnable<Boolean> cir) {
		LivingEntity self = (LivingEntity) (Object) this;
		// client only: on the server this is also what lets projectiles hit, and mobs' arrows should hit proxies
		if (!MobWar.VANILLA_COMBAT && self.level().isClientSide() && MobWar.isProxy(self)) {
			cir.setReturnValue(false);
		}
	}
}
