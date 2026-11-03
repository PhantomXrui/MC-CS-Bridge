package dev.rehan.passthrough.mixin;

import dev.rehan.passthrough.HostPlayerBody;
import net.minecraft.core.BlockPos;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.item.BlockItem;
import net.minecraft.world.item.context.BlockPlaceContext;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.shapes.CollisionContext;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;

/** Keep vanilla placement, substituting only the host-controlled player's occupied volume. */
@Mixin(BlockItem.class)
abstract class HostBlockPlacementMixin {
    @Redirect(method="canPlace", at=@At(value="INVOKE", target="Lnet/minecraft/world/level/Level;isUnobstructed(Lnet/minecraft/world/level/block/state/BlockState;Lnet/minecraft/core/BlockPos;Lnet/minecraft/world/phys/shapes/CollisionContext;)Z"))
    private boolean passthrough$hostBody(Level level, BlockState state, BlockPos position, CollisionContext collision,
                                        BlockPlaceContext placement, BlockState ignored) {
        Player player=placement.getPlayer();
        HostPlayerBody.Snapshot host=player==null?null:HostPlayerBody.currentFor(player);
        if (host==null || player==null) return level.isUnobstructed(state,position,collision);
        var shape=state.getCollisionShape(level,position,collision).move(position);
        if (shape.isEmpty()) return true;
        for (var box:shape.toAabbs()) {
            if (host.bounds().intersects(box.minX,box.minY,box.minZ,box.maxX,box.maxY,box.maxZ)) return false;
        }
        // MC's copy can be one packet behind and is narrower than a native CS pawn.
        // Check all other vanilla entities after checking this player's authoritative host body.
        return level.isUnobstructed(player,shape);
    }
}
