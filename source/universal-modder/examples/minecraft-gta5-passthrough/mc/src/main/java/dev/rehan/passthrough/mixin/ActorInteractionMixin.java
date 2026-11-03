package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.ActorInteractions;
import dev.rehan.passthrough.MobWar;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.npc.villager.Villager;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.phys.Vec3;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Before vanilla mob item handling: host proxies must not trade or produce MC villagers. */
@Mixin(Mob.class)
abstract class ActorInteractionMixin {
    @Inject(method = "interact", at = @At("HEAD"), cancellable = true)
    private void passthrough$actorInteract(Player player, InteractionHand hand, Vec3 hit,
            CallbackInfoReturnable<InteractionResult> cir) {
        if (MobWar.VANILLA_COMBAT && (Object)this instanceof Villager actor && MobWar.isProxy(actor))
            cir.setReturnValue(ActorInteractions.interact(actor, player, hand));
    }
}
