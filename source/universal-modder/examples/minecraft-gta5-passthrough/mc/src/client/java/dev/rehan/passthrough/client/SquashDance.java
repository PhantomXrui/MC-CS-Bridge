package dev.rehan.passthrough.client;

import com.google.gson.JsonObject;
import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.math.Axis;
import net.minecraft.client.renderer.entity.state.AvatarRenderState;
import net.minecraft.client.renderer.entity.state.EnderDragonRenderState;
import net.minecraft.client.renderer.entity.state.EntityRenderState;
import net.minecraft.client.renderer.entity.state.LivingEntityRenderState;

/** A local-bridge, render-only dance. No entity size, collision, AI or visibility is changed. */
public final class SquashDance {
	private record Session(Object owner, long startMs, double bpm) { }
	public record Scale(float horizontal, float vertical, float yawDegrees) { }
	private static volatile Session session;
	private static volatile Scale frameScale;
	private static volatile long rendered;
	private static volatile long renderedPlayers;
	private static volatile long renderedDragons;

	private SquashDance() { }

	/** Repeated messages with the same epoch retain phase; malformed messages never replace good state. */
	static synchronized void receive(JsonObject message, Object owner) {
		if (!message.has("on") || !message.get("on").isJsonPrimitive()
			|| !message.getAsJsonPrimitive("on").isBoolean()) {
			throw new IllegalArgumentException("dance.on must be boolean");
		}
		if (!message.get("on").getAsBoolean()) {
			reset();
			return;
		}
		double bpm = 126.0;
		if (message.has("bpm")) {
			if (!message.get("bpm").isJsonPrimitive() || !message.getAsJsonPrimitive("bpm").isNumber()) {
				throw new IllegalArgumentException("dance.bpm must be numeric");
			}
			bpm = message.get("bpm").getAsDouble();
		}
		if (!Double.isFinite(bpm) || bpm < 40.0 || bpm > 240.0) {
			throw new IllegalArgumentException("dance.bpm must be 40..240");
		}
		long now = System.currentTimeMillis();
		long startMs = now;
		if (message.has("startMs")) {
			if (!message.get("startMs").isJsonPrimitive() || !message.getAsJsonPrimitive("startMs").isNumber()) {
				throw new IllegalArgumentException("dance.startMs must be an integer Unix timestamp");
			}
			startMs = message.get("startMs").getAsBigDecimal().longValueExact();
		}
		if (startMs <= 0 || startMs > now + 30_000L) {
			throw new IllegalArgumentException("dance.startMs is outside the supported epoch range");
		}
		Session previous = session;
		if (previous == null || previous.startMs != startMs || previous.bpm != bpm) {
			rendered = renderedPlayers = renderedDragons = 0;
		}
		session = new Session(owner, startMs, bpm);
        dev.rehan.passthrough.DanceGather.receive(true,startMs,message.has("gatherRequest")?message.get("gatherRequest").getAsLong():0);
	}

	/** A diagnostic client disconnect must not cancel the host's dance. */
	static synchronized void disconnected(Object owner) {
		Session active = session;
		if (active != null && active.owner == owner) reset();
	}

	private static void reset() {
        dev.rehan.passthrough.DanceGather.receive(false,0,0);
		session = null;
		frameScale = null;
	}

	/** Capture once per exported frame so every entity gets exactly the same phase. */
	static void beginFrame(boolean hostActive) {
		Session active = session;
		frameScale = hostActive && active != null
			? sample(System.currentTimeMillis(), active.startMs, active.bpm) : null;
	}

	public static Scale sample(long nowMs, long startMs, double bpm) {
		double beats = Math.max(0.0, (nowMs - startMs) * bpm / 60_000.0);
		double squash = (1.0 - Math.cos((beats - Math.floor(beats)) * Math.PI * 2.0)) * 0.5;
		// One complete turn per four beats, using the same epoch as the native CS renderer.
		float yawDegrees = (float) ((beats % 4.0) * 90.0);
		return new Scale((float) (1.0 + 0.55 * squash), (float) (1.0 - 0.82 * squash), yawDegrees);
	}

	/** Called after dispatcher world translation, before the model's own local transforms. */
	public static boolean apply(EntityRenderState state, PoseStack poses) {
		// Only our textured display participates; ordinary display entities and paintings stay unchanged.
		boolean memePlane = false;
		if (state instanceof net.minecraft.client.renderer.entity.state.ItemDisplayEntityRenderState display
			&& !display.item.isEmpty()) {
			var material = display.item.pickParticleMaterial(net.minecraft.util.RandomSource.create(0));
			memePlane = material != null && material.sprite().contents().name().toString().equals("mccs:item/meme_chicken");
		}
		Scale scale = frameScale;
		if (session == null || scale == null || state.isInvisible
			|| !(memePlane || state instanceof LivingEntityRenderState || state instanceof EnderDragonRenderState)) return false;
		poses.pushPose();
		if (!memePlane) poses.rotateDegrees(Axis.YP, scale.yawDegrees);
		float memeBase = memePlane && state instanceof net.minecraft.client.renderer.entity.state.ItemDisplayEntityRenderState display
			&& display.renderState != null ? display.renderState.transformation().get(display.interpolationProgress).translation().y() : 0.0F;
		// A ridden display compensates its mount height; squash around that same ground-level pivot.
		if (memePlane) poses.translate(0, memeBase, 0);
		poses.scale(scale.horizontal, scale.vertical, memePlane ? 1.0F : scale.horizontal);
		if (memePlane) poses.translate(0, -memeBase, 0);
		rendered++;
		if (state instanceof AvatarRenderState) renderedPlayers++;
		if (state instanceof EnderDragonRenderState) renderedDragons++;
		return true;
	}

	static JsonObject inspect() {
		JsonObject result = new JsonObject();
		Session active = session;
		Scale scale = frameScale;
		result.addProperty("t", "dancestate");
		result.add("gather",dev.rehan.passthrough.DanceGather.inspect());
		result.addProperty("on", active != null);
		result.addProperty("renderActive", active != null && scale != null);
		if (active != null) {
			result.addProperty("startMs", active.startMs);
			result.addProperty("bpm", active.bpm);
		}
		result.addProperty("horizontal", scale == null ? 1.0F : scale.horizontal);
		result.addProperty("vertical", scale == null ? 1.0F : scale.vertical);
		result.addProperty("yawDegrees", scale == null ? 0.0F : scale.yawDegrees);
		result.addProperty("rendered", rendered);
		result.addProperty("renderedPlayers", renderedPlayers);
		result.addProperty("renderedDragons", renderedDragons);
		result.addProperty("memePlaneSupported", true);
		result.addProperty("memePlaneYawDegrees", 0.0F);
		return result;
	}
}
