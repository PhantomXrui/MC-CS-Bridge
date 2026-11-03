package dev.rehan.passthrough.client;

import net.fabricmc.fabric.api.client.rendering.v1.hud.HudElement;
import net.minecraft.client.DeltaTracker;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.Font;
import net.minecraft.client.gui.GuiGraphicsExtractor;

/**
 * 常驻画面右下角的 ID。
 *
 * <p>画在 HUD 层（frame export 的 overlay 层）：合成器对 overlay 是无条件叠加的，不做深度判定，
 * 所以它不会像世界里的 text_display 那样“只在方块表面上出现”，而是任何位置、任何角度都在画面上。
 */
public final class WatermarkHud implements HudElement {
	private static final String BRAND = "Bilibili:";
	private static final String NAME = "Phantom_rui";

	@Override
	public void extractRenderState(final GuiGraphicsExtractor graphics, final DeltaTracker delta) {
		final Minecraft minecraft = Minecraft.getInstance();
		if (minecraft == null || minecraft.font == null) {
			return;
		}
		final Font font = minecraft.font;
		final int brandWidth = font.width(BRAND);
		final int totalWidth = brandWidth + font.width(NAME);
		// 居中，放在血条 / 饥饿值上方一点点。
		final int x = (graphics.guiWidth() - totalWidth) / 2;
		final int y = graphics.guiHeight() - 52;
		graphics.text(font, BRAND, x, y, 0xFFFB7299, true);
		graphics.text(font, NAME, x + brandWidth, y, 0xFFFFFFFF, true);
	}
}
