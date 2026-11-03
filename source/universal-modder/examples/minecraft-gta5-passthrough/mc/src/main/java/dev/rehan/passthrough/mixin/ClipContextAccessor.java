package dev.rehan.passthrough.mixin;

import net.minecraft.world.level.ClipContext;
import net.minecraft.world.phys.shapes.CollisionContext;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;

/** Preserve the actual ray owner, including server-side item POV traces. */
@Mixin(ClipContext.class)
public interface ClipContextAccessor {
    @Accessor("collisionContext")
    CollisionContext passthrough$rayCollisionContext();
}
