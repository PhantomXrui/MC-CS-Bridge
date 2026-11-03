package dev.rehan.passthrough.mixin;
import dev.rehan.passthrough.HostVitals;
import net.minecraft.world.entity.LivingEntity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Unique;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
@Mixin(LivingEntity.class)
abstract class PlayerVitalsMixin {
    @Unique private float passthrough$oldAbsorption;
    @Inject(method="heal",at=@At("HEAD"),cancellable=true)
    private void passthrough$heal(float amount,CallbackInfo ci){if(HostVitals.heal((LivingEntity)(Object)this,amount))ci.cancel();}
    @Inject(method="setAbsorptionAmount",at=@At("HEAD"))
    private void passthrough$absBefore(float amount,CallbackInfo ci){passthrough$oldAbsorption=((LivingEntity)(Object)this).getAbsorptionAmount();}
    @Inject(method="setAbsorptionAmount",at=@At("RETURN"))
    private void passthrough$absAfter(float amount,CallbackInfo ci){var self=(LivingEntity)(Object)this;HostVitals.absorption(self,passthrough$oldAbsorption,self.getAbsorptionAmount());}
}
