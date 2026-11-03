package dev.rehan.passthrough.client.mixin;
import dev.rehan.passthrough.HostVitals;
import net.minecraft.client.gui.Hud;
import net.minecraft.client.multiplayer.MultiPlayerGameMode;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;
/** Keep the creative inventory, but draw vanilla health, food, absorption and effect hearts. */
@Mixin(Hud.class)
abstract class HostVitalsHudMixin {
    @Redirect(method={"extractHotbarAndDecorations","extractSelectedItemName"},at=@At(value="INVOKE",target="Lnet/minecraft/client/multiplayer/MultiPlayerGameMode;canHurtPlayer()Z"))
    private boolean passthrough$showVitals(MultiPlayerGameMode mode){return HostVitals.enabled()||mode.canHurtPlayer();}
}
