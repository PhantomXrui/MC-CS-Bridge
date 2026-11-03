package dev.rehan.passthrough;

import net.minecraft.core.particles.ParticleTypes;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.effect.MobEffectCategory;
import net.minecraft.world.effect.MobEffectInstance;
import net.minecraft.world.entity.AreaEffectCloud;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.animal.chicken.Chicken;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.entity.projectile.Projectile;
import net.minecraft.world.entity.projectile.hurtingprojectile.WitherSkull;

/** Baby chicks are spectators in the CS mob battle; adults and player interactions stay vanilla. */
public final class BabyChickenProtection {
    private BabyChickenProtection() {}

    public static boolean protectedChick(Entity entity) {
        // Do not tie this to the render/connection heartbeat: alt-tab must not remove protection.
        return MobWar.VANILLA_COMBAT && entity instanceof Chicken chicken && chicken.isBaby();
    }

    public static boolean blocksDamage(Entity victim, DamageSource source) {
        if (!protectedChick(victim)) return false;
        Entity attacker = owner(source.getEntity());
        Entity direct = owner(source.getDirectEntity());
        // A player-owned projectile (including a deflected one) remains an intentional player action.
        if (attacker instanceof Player || direct instanceof Player) return false;
        return enemy(attacker) || enemy(direct);
    }

    public static boolean blocksEffect(Entity victim, MobEffectInstance effect, Entity source) {
        return protectedChick(victim)
            && effect.getEffect().value().getCategory() == MobEffectCategory.HARMFUL
            && enemy(owner(source));
    }

    public static boolean blocksProjectileHit(Entity victim, Projectile projectile) {
        return protectedChick(victim) && enemy(owner(projectile));
    }

    private static boolean enemy(Entity entity) {
        if (entity == null || entity instanceof Player) return false;
        if (MobWar.fighter(entity) || MobWar.isProxy(entity)) return true;
        // EntityReference drops removed owners. These two vanilla boss hazards keep their identity
        // after the boss dies; ordinary ownerless magic, player potions and other clouds stay vanilla.
        if (entity instanceof WitherSkull skull) return skull.getOwner() == null;
        return entity instanceof AreaEffectCloud cloud && cloud.getOwner() == null
            && cloud.getParticle().getType() == ParticleTypes.DRAGON_BREATH;
    }

    private static Entity owner(Entity entity) {
        // Clouds and projectiles sometimes arrive as the direct source rather than their owner.
        for (int depth = 0; entity != null && depth < 4; depth++) {
            Entity next = entity instanceof Projectile projectile ? projectile.getOwner()
                : entity instanceof AreaEffectCloud cloud ? cloud.getOwner() : null;
            if (next == null || next == entity) break;
            entity = next;
        }
        return entity;
    }
}
