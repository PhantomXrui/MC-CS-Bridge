package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.BabyChickenProtection;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.projectile.hurtingprojectile.WitherSkull;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;

/** An orphaned skull uses anonymous magic damage, so retain its identity at the impact call site. */
@Mixin(WitherSkull.class)
abstract class BabyChickenWitherSkullMixin {
    @Redirect(method = "onHitEntity",
        at = @At(value = "INVOKE",
            target = "Lnet/minecraft/world/entity/Entity;hurtServer(Lnet/minecraft/server/level/ServerLevel;Lnet/minecraft/world/damagesource/DamageSource;F)Z"),
        require = 2)
    private boolean passthrough$protectChickFromSkull(Entity victim, ServerLevel level,
                                                    DamageSource source, float amount) {
        if (BabyChickenProtection.blocksProjectileHit(victim, (WitherSkull) (Object) this)) {
            return false;
        }
        // Leave onHit's explosion/removal intact and preserve player-owned skulls and adult damage.
        return victim.hurtServer(level, source, amount);
    }
}
