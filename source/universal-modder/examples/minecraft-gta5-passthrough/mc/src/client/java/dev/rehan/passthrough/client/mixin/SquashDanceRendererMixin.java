package dev.rehan.passthrough.client.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import com.mojang.blaze3d.vertex.PoseStack;
import dev.rehan.passthrough.client.SquashDance;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.client.renderer.entity.EntityRenderDispatcher;
import net.minecraft.client.renderer.entity.EntityRenderer;
import net.minecraft.client.renderer.entity.state.EntityRenderState;
import net.minecraft.client.renderer.state.level.CameraRenderState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

/** 26.3 dispatcher signature verified against the installed game, including the special dragon renderer. */
@Mixin(EntityRenderDispatcher.class)
abstract class SquashDanceRendererMixin {
	@WrapOperation(method = "submit", at = @At(value = "INVOKE",
		target = "Lnet/minecraft/client/renderer/entity/EntityRenderer;submit(Lnet/minecraft/client/renderer/entity/state/EntityRenderState;Lcom/mojang/blaze3d/vertex/PoseStack;Lnet/minecraft/client/renderer/SubmitNodeCollector;Lnet/minecraft/client/renderer/state/level/CameraRenderState;)V"))
	private void passthrough$squashDance(EntityRenderer<?, ?> renderer, EntityRenderState state,
		PoseStack poses, SubmitNodeCollector collector, CameraRenderState camera, Operation<Void> original) {
		boolean transformed = SquashDance.apply(state, poses);
		try {
			original.call(renderer, state, poses, collector, camera);
		} finally {
			if (transformed) poses.popPose();
		}
	}
}
