package dev.rehan.passthrough.client.mixin;

import com.mojang.blaze3d.vertex.PoseStack;
import dev.rehan.passthrough.Passthrough;
import dev.rehan.passthrough.WorldBridge;
import net.minecraft.client.Minecraft;
import net.minecraft.client.renderer.LevelRenderer;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.client.renderer.state.level.LevelRenderState;
import net.minecraft.world.level.block.Blocks;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Keep normal MC block selection; never draw the host terrain's invisible support cells. */
@Mixin(LevelRenderer.class)
abstract class HostOutlineMixin {
    @Inject(method="submitBlockOutline",at=@At("HEAD"),cancellable=true)
    private void passthrough$hideHostSupport(PoseStack poses,SubmitNodeCollector collector,LevelRenderState state,CallbackInfo ci){
        var outline=state.blockOutlineRenderState;var level=Minecraft.getInstance().level;
        // Persisted support can be outside this session's streamed ownership set.
        // All invisible barriers must stay visually invisible, even before reclamation.
        if(Passthrough.active && outline!=null && level!=null
                && level.getBlockState(outline.pos()).is(Blocks.BARRIER))ci.cancel();
    }
}
