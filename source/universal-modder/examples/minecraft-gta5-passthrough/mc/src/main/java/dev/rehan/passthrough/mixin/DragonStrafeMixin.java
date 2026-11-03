package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.MobWar;
import dev.rehan.passthrough.Passthrough;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.boss.enderdragon.phases.DragonStrafePlayerPhase;
import net.minecraft.world.phys.Vec3;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Keep native dragon steering/fireballs, but approach host actors instead of End-island waypoints. */
@Mixin(DragonStrafePlayerPhase.class)
abstract class DragonStrafeMixin {
    @Shadow private LivingEntity attackTarget;

    @Inject(method="getFlyTargetLocation",at=@At("HEAD"),cancellable=true)
    private void passthrough$actorApproach(CallbackInfoReturnable<Vec3> cir) {
        if(MobWar.VANILLA_COMBAT && Passthrough.active && attackTarget!=null
                && attackTarget.isAlive() && MobWar.isEnemyProxy(attackTarget))
            cir.setReturnValue(attackTarget.position().add(0,8,0));
    }
}
