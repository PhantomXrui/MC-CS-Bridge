package dev.rehan.passthrough.client.mixin;

import dev.rehan.passthrough.ActorInteractions;
import dev.rehan.passthrough.MobWar;
import dev.rehan.passthrough.Passthrough;
import net.minecraft.client.Minecraft;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.entity.npc.villager.Villager;
import net.minecraft.world.item.Items;
import net.minecraft.world.phys.EntityHitResult;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Entity-targeted host interactions must never fall through to egg/item throwing. */
@Mixin(Minecraft.class)
abstract class ActorUseItemMixin {
    @Shadow private int rightClickDelay;

    @Inject(method = "startUseItem", at = @At("HEAD"), cancellable = true)
    private void passthrough$actorUse(CallbackInfo ci) {
        Minecraft minecraft = (Minecraft)(Object)this;
        if (!Passthrough.active || !MobWar.VANILLA_COMBAT || minecraft.player == null || minecraft.gameMode == null
                || !(minecraft.hitResult instanceof EntityHitResult hit)
                || !(hit.getEntity() instanceof Villager actor) || !MobWar.isProxy(actor)) return;
        var player = minecraft.player;
        // The user reserved enchanted apples for ordinary consumption, never breeding.
        // Preserve the main hand's vanilla use priority even if the offhand holds actor food.
        if (player.getMainHandItem().is(Items.ENCHANTED_GOLDEN_APPLE)) return;
        InteractionHand selected = null;
        for (InteractionHand hand : InteractionHand.values()) {
            if (ActorInteractions.hasInteractionItem(actor, player, hand)) { selected = hand; break; }
        }
        if (selected == null) return;
        // Retain vanilla's normal mining/busy guards and four-tick use cadence.
        // In particular, an out-of-range entity hit is consumed instead of reaching useItem.
        ci.cancel();
        if (minecraft.gameMode.isDestroying()) return;
        rightClickDelay = 4;
        if (player.isHandsBusy() || !actor.level().getWorldBorder().isWithinBounds(actor.blockPosition())
                || !player.isWithinEntityInteractionRange(actor, 0)) return;
        var stack = player.getItemInHand(selected);
        if (!stack.isItemEnabled(actor.level().enabledFeatures())) return;
        // One vanilla entity packet; the integrated server remains authoritative for team, life,
        // range, inventory, feeding cooldown and duplicate events. Never send a use-item packet.
        InteractionResult result = minecraft.gameMode.interact(player, actor, hit, selected);
        if (result instanceof InteractionResult.Success success
                && success.swingSource() == InteractionResult.SwingSource.PREDICTED)
            player.swing(selected, stack.getInteractAnimation(), false);
    }
}
