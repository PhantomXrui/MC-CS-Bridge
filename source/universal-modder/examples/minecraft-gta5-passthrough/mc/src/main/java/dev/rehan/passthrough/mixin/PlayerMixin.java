package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.Passthrough;
import dev.rehan.passthrough.HostPlayerBody;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.Pose;
import net.minecraft.world.entity.player.Player;
import org.objectweb.asm.Opcodes;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Legacy host-follow copies are ghosts. Native MC movement restores the normal
 * player body; HostTerrain supplies the map's additional collision shapes.
 */
@Mixin(Player.class)
abstract class PlayerMixin {
	@Inject(method = "updatePlayerPose", at = @At("HEAD"), cancellable = true)
	private void passthrough$hostPosture(final CallbackInfo ci) {
		Player player=(Player)(Object)this;
		HostPlayerBody.Snapshot host=HostPlayerBody.currentFor(player);
		if (host==null) return;
		if (player.isPassenger() || player.isSleeping() || player.isFallFlying()) return;
		// Host-following uses creative flight to suppress gravity. Vanilla therefore refuses
		// crouching even with Shift held; the host's actual posture remains authoritative.
		player.setShiftKeyDown(host.sneak());
		player.setPose(host.sneak()?Pose.CROUCHING:Pose.STANDING);
		player.setOnGround(host.grounded());
		ci.cancel();
	}

	@Inject(method = "tick", at = @At(value = "FIELD", target = "Lnet/minecraft/world/entity/player/Player;noPhysics:Z", opcode = Opcodes.PUTFIELD, shift = At.Shift.AFTER))
	private void passthrough$ghost(final CallbackInfo ci) {
		Player player=(Player)(Object)this;
		HostPlayerBody.updateMovementAbilities(player);
		if (Passthrough.active && !HostPlayerBody.minecraftMovement(player)) {
			player.noPhysics = true;
		}
	}
}
