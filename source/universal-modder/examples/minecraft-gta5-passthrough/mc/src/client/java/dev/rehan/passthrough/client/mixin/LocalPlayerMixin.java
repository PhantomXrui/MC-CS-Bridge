package dev.rehan.passthrough.client.mixin;

import dev.rehan.passthrough.client.PlayerSync;
import dev.rehan.passthrough.HostPlayerBody;
import net.minecraft.client.player.LocalPlayer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

@Mixin(LocalPlayer.class)
abstract class LocalPlayerMixin {
    @Inject(method="suffocatesAt",at=@At("HEAD"),cancellable=true)
    private void passthrough$ignoreCoarsePushOut(net.minecraft.core.BlockPos pos,CallbackInfoReturnable<Boolean> cir){
        Boolean solid=dev.rehan.passthrough.HostTerrain.suffocatesAt((LocalPlayer)(Object)this,pos);
        if(solid!=null)cir.setReturnValue(solid);
    }
	/** After the old position was saved for interpolation and before movement is sent to the server. */
	@Inject(method = "tick", at = @At("HEAD"), cancellable=true)
	private void passthrough$followHost(final CallbackInfo ci) {
		PlayerSync.tick((LocalPlayer) (Object) this);
		if (PlayerSync.waitingForMovement()) ci.cancel();
	}

	@Inject(method="tick", at=@At("TAIL"))
	private void passthrough$publishMovement(final CallbackInfo ci) {
		PlayerSync.afterTick((LocalPlayer)(Object)this);
	}

	@Inject(method="sendChanges", at=@At("HEAD"))
	private void passthrough$sendHostFeet(final CallbackInfo ci) {
		PlayerSync.beforeSend((LocalPlayer)(Object)this);
	}

	@Inject(method="aiStep",at=@At(value="INVOKE",target="Lnet/minecraft/client/player/AbstractClientPlayer;aiStep()V",shift=At.Shift.AFTER),cancellable=true)
	private void passthrough$keepHostFlight(final CallbackInfo ci) {
		// Installed 26.3's only remaining aiStep code disables creative flight on landing.
		// Host grounded state must not toggle our gravity-suppression ability off every tick.
		if (HostPlayerBody.current()!=null) ci.cancel();
	}

	@Inject(method="isCrouching", at=@At("HEAD"), cancellable=true)
	private void passthrough$hostCrouch(final CallbackInfoReturnable<Boolean> cir) {
		var host=HostPlayerBody.current();
		if (host!=null) cir.setReturnValue(host.sneak());
	}

	@Inject(method="isShiftKeyDown", at=@At("HEAD"), cancellable=true)
	private void passthrough$hostSneak(final CallbackInfoReturnable<Boolean> cir) {
		var host=HostPlayerBody.current();
		if (host!=null) cir.setReturnValue(host.sneak());
	}
}
