package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.BabyChickenProtection;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.effect.MobEffectInstance;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.LivingEntity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

@Mixin(LivingEntity.class)
abstract class BabyChickenProtectionMixin {
    @Inject(method = "hurtServer(Lnet/minecraft/server/level/ServerLevel;Lnet/minecraft/world/damagesource/DamageSource;F)Z",
        at = @At("HEAD"), cancellable = true)
    private void passthrough$protectChick(ServerLevel level, DamageSource source, float amount,
                                         CallbackInfoReturnable<Boolean> cir) {
        if (BabyChickenProtection.blocksDamage((LivingEntity) (Object) this, source)) {
            cir.setReturnValue(false);
        }
    }

    @Inject(method = "addEffect(Lnet/minecraft/world/effect/MobEffectInstance;Lnet/minecraft/world/entity/Entity;)Z",
        at = @At("HEAD"), cancellable = true)
    private void passthrough$protectChickEffect(MobEffectInstance effect, Entity source,
                                               CallbackInfoReturnable<Boolean> cir) {
        // Stop attributed wither/poison at application, before later damage ticks lose their attacker.
        if (BabyChickenProtection.blocksEffect((LivingEntity) (Object) this, effect, source)) {
            cir.setReturnValue(false);
        }
    }
}
