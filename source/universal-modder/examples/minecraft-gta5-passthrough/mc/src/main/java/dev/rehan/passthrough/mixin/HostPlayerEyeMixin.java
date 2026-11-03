package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.HostPlayerBody;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.Pose;
import net.minecraft.world.entity.player.Player;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** All native rays and projectile origins use the same eye as the host-controlled human. */
@Mixin(Entity.class)
abstract class HostPlayerEyeMixin {
    private HostPlayerBody.Snapshot passthrough$eye() {
        if (!((Object)this instanceof Player player)) return null;
        var body=HostPlayerBody.currentFor(player);
        return body!=null && Float.isFinite(body.eyeHeight())?body:null;
    }

    @Inject(method="getEyeHeight()F",at=@At("HEAD"),cancellable=true)
    private void passthrough$hostEyeHeight(CallbackInfoReturnable<Float> cir) {
        var body=passthrough$eye();
        if(body!=null) cir.setReturnValue(body.eyeHeight());
    }

    @Inject(method="getEyeHeight(Lnet/minecraft/world/entity/Pose;)F",at=@At("HEAD"),cancellable=true)
    private void passthrough$hostPoseEyeHeight(Pose pose,CallbackInfoReturnable<Float> cir) {
        var body=passthrough$eye();
        if(body!=null) cir.setReturnValue(body.eyeHeight());
    }

    // getEyeY reads the cached field directly, not getEyeHeight(), in installed 26.3.
    @Inject(method="getEyeY",at=@At("HEAD"),cancellable=true)
    private void passthrough$hostEyeY(CallbackInfoReturnable<Double> cir) {
        var body=passthrough$eye();
        if(body!=null) cir.setReturnValue(((Entity)(Object)this).getY()+body.eyeHeight());
    }
}
