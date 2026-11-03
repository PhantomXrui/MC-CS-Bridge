package dev.rehan.passthrough.client;

import net.minecraft.client.CameraType;
import net.minecraft.client.Minecraft;
import dev.rehan.passthrough.Passthrough;
import dev.rehan.passthrough.HostPlayerBody;
import dev.rehan.passthrough.HostTerrain;
import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import dev.rehan.passthrough.client.mixin.KeyMappingAccessor;
import java.util.Locale;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.world.entity.player.Abilities;
import net.minecraft.world.entity.Pose;
import net.minecraft.world.entity.player.Input;
import net.minecraft.world.phys.Vec3;

/** Keeps the Minecraft player on the host's player: it stands where they stand and looks where the host camera looks. */
public final class PlayerSync {
	private static final double TELEPORT_SQ = 64.0 * 64.0;
	/** How far the host's player moved over the last client tick (drives the walk animation). */
	private static float tickDistance;
	private static double lastX = Double.NaN, lastZ;
	private static boolean hostSneakApplied;
	private static LocalPlayer movementPlayer;
	private static long movementSession, stateSequence, lastFramePublishNanos, lastStateNanos;
	private static volatile long generation;
	private static volatile long serverAnchorGeneration;
	private static boolean anchorQueued, movementReady;
	private static String movementStatus="off";

	private PlayerSync() {
	}

	public static float tickDistance() {
		return tickDistance;
	}

	/** Every frame, before the camera update: position, rotation, and first/third person to match the host. */
	public static void frame(final float partialTick) {
		HostState.Pose p = HostState.frame();
		Minecraft minecraft = Minecraft.getInstance();
		LocalPlayer player = minecraft.player;
		if (p == null || player == null) {
			return;
		}
		HostPlayerBody.bind(player);
		if (HostPlayerBody.minecraftMovement()) {
			look(player,p);
			view(minecraft,p);
			publishMovement(player,partialTick,false);
			return;
		}

		if (p.drive()) {
			// Minecraft flies the player (elytra): it only takes where to look, and tells the host where it is
			player.setYRot(p.lookYaw());
			player.setXRot(p.lookPitch());
			player.yRotO = p.lookYaw();
			player.xRotO = p.lookPitch();
			player.yHeadRot = player.yHeadRotO = p.lookYaw();
			if (minecraft.options.getCameraType() != CameraType.THIRD_PERSON_BACK) {
				minecraft.options.setCameraType(CameraType.THIRD_PERSON_BACK);
			}
			// airborne: with no collision onGround never updates, and the server cancels a grounded player's glide
			player.setOnGround(false);

			// where the player is drawn this frame, how fast that moves (the slope of the tick interpolation, per
			// second) and when (System.nanoTime is the host's QueryPerformanceCounter clock): the host carries it forward
			Vec3 at = player.getPosition(partialTick);
			double vx = (player.getX() - player.xo) * 20.0, vy = (player.getY() - player.yo) * 20.0, vz = (player.getZ() - player.zo) * 20.0;
			Passthrough.events.accept(String.format(Locale.ROOT, "{\"t\":\"mcpos\",\"pos\":[%.4f,%.4f,%.4f],\"vel\":[%.3f,%.3f,%.3f],\"tn\":%d,\"fly\":%b}",
				at.x, at.y, at.z, vx, vy, vz, System.nanoTime(), player.isFallFlying()));
			return;
		}

		// Front view reverses the camera, never the player's aim or head.
		player.setYRot(p.lookYaw());
		player.setXRot(p.lookPitch());
		player.yRotO = p.lookYaw();
		player.xRotO = p.lookPitch();
		player.yHeadRot = player.yHeadRotO = p.lookYaw();
		player.yBodyRot = player.yBodyRotO = p.firstPerson() ? p.yaw() : p.bodyYaw();
		// the model stands exactly where the host's player is this frame (not a tick behind, interpolating)
		applyHostPosture(player);
		double x = p.hasPlayerFeet() ? p.px() : p.x();
		double y = p.hasPlayerFeet() ? p.py() : p.y() - player.getEyeHeight();
		double z = p.hasPlayerFeet() ? p.pz() : p.z();
		player.setPos(x, y, z);
		player.xo = player.xOld = x;
		player.yo = player.yOld = y;
		player.zo = player.zOld = z;
		CameraType cameraType = p.firstPerson() ? CameraType.FIRST_PERSON : p.front() ? CameraType.THIRD_PERSON_FRONT : CameraType.THIRD_PERSON_BACK;
		if (minecraft.options.getCameraType() != cameraType) {
			minecraft.options.setCameraType(cameraType);
		}
	}

	/**
	 * Every client tick, at the start of the player's tick (the old position is already saved, so the model
	 * interpolates and walks): gameplay always uses host feet; CameraMixin separately uses the host eye.
	 */
	public static void tick(final LocalPlayer player) {
		HostPlayerBody.bind(player);
		HostState.Pose p = HostState.live();
		if (prepareMovement(player,p)) return;
		applyHostSneak(player);
		if (p == null || p.drive()) {
			return;
		}

		applyHostPosture(player);
		double x = p.hasPlayerFeet() ? p.px() : p.x();
		double y = p.hasPlayerFeet() ? p.py() : p.y() - player.getEyeHeight();
		double z = p.hasPlayerFeet() ? p.pz() : p.z();
		tickDistance = Double.isNaN(lastX) ? 0.0F : (float) Math.min(Math.hypot(x - lastX, z - lastZ), 1.0);
		lastX = x;
		lastZ = z;
		boolean teleport = player.distanceToSqr(x, y, z) > TELEPORT_SQ;
		player.setPos(x, y, z);
		if (teleport) {
			player.xo = player.xOld = x;
			player.yo = player.yOld = y;
			player.zo = player.zOld = z;
		}

		player.setDeltaMovement(Vec3.ZERO);
		Abilities abilities = player.getAbilities();
		if (abilities.mayfly && !abilities.flying) {
			abilities.flying = true;
			player.onUpdateAbilities();
		}
	}

	/** Only input and orientation are injected: vanilla LocalPlayer supplies all motion. */
	private static boolean prepareMovement(LocalPlayer player, HostState.Pose pose) {
		var request=HostPlayerBody.movement();
		Minecraft minecraft=Minecraft.getInstance();
		if (request==null) {
			if (movementSession!=0) {
				ClientInput.resetMovement(minecraft);
				movementSession=0;movementPlayer=null;movementReady=false;anchorQueued=false;
				movementStatus="off";generation++;lastX=Double.NaN;
			}
			HostPlayerBody.updateMovementAbilities(player);
			return false;
		}
		if (movementSession!=request.session() || movementPlayer!=player) {
			movementSession=request.session();movementPlayer=player;generation++;
			movementReady=false;anchorQueued=false;movementStatus="waiting_geometry";
			lastFramePublishNanos=0;hostSneakApplied=false;
			ClientInput.beginMovement(minecraft,movementSession);
			player.noPhysics=player.isSpectator();
		}
		HostPlayerBody.updateMovementAbilities(player);
		if (pose!=null) {look(player,pose);view(minecraft,pose);}
		if (!HostTerrain.movementReady()) {
			movementStatus="waiting_geometry";
			ClientInput.applyMovement(minecraft,false);
			return true;
		}
		if (!anchorQueued) {
			var server=minecraft.getSingleplayerServer();
			if (server==null) {
				movementStatus="waiting_server";ClientInput.applyMovement(minecraft,false);return true;
			}
			anchorQueued=true;movementStatus="waiting_anchor";
			long anchorGeneration=generation, session=movementSession;
			var uuid=player.getUUID();
			float yaw=pose==null?player.getYRot():pose.lookYaw(),pitch=pose==null?player.getXRot():pose.lookPitch();
			server.execute(()->{
				var live=HostPlayerBody.movement();
				if (live==null || live.session()!=session || generation!=anchorGeneration) return;
				var authoritative=server.getPlayerList().getPlayer(uuid);
				if (authoritative==null) return;
				authoritative.stopRiding();
				HostPlayerBody.updateMovementAbilities(authoritative);
				authoritative.setDeltaMovement(Vec3.ZERO);authoritative.setOnGround(false);
				// Server's vanilla teleport packet establishes both copies once. Camera/pl are never corrections.
				authoritative.connection.teleport(request.x(),request.y(),request.z(),yaw,pitch);
				serverAnchorGeneration=anchorGeneration;
			});
		}
		if (!movementReady && serverAnchorGeneration==generation
			&& player.distanceToSqr(request.x(),request.y(),request.z())<0.25) {
			movementReady=true;movementStatus="ready";
		}
		ClientInput.applyMovement(minecraft,movementReady);
		if (movementReady) movementStatus="ready";
		return true;
	}

	/** During loading/one-time spawn handshake no local physics runs against stale geometry. */
	public static boolean waitingForMovement() {
		return HostPlayerBody.minecraftMovement() && (!movementReady || !HostTerrain.movementReady());
	}
	public static void afterTick(LocalPlayer player) {
		ClientInput.endMovementTick();
		publishMovement(player,1.0F,true);
	}
	private static void look(LocalPlayer player,HostState.Pose pose) {
		player.setYRot(pose.lookYaw());player.setXRot(pose.lookPitch());
		player.yRotO=pose.lookYaw();player.xRotO=pose.lookPitch();
		player.yHeadRot=pose.lookYaw();
		// Vanilla updates body rotation, walking, swimming and riding animations.
	}
	private static void view(Minecraft minecraft,HostState.Pose pose) {
		CameraType camera=pose.firstPerson()?CameraType.FIRST_PERSON:pose.front()?CameraType.THIRD_PERSON_FRONT:CameraType.THIRD_PERSON_BACK;
		if (minecraft.options.getCameraType()!=camera) minecraft.options.setCameraType(camera);
	}
	private static JsonArray vector(Vec3 v) {
		JsonArray a=new JsonArray();a.add(v.x);a.add(v.y);a.add(v.z);return a;
	}
	private static void publishMovement(LocalPlayer player,float partialTick,boolean tick) {
		var request=HostPlayerBody.movement();
		if (request==null || request.session()!=movementSession || !HostPlayerBody.minecraftMovement(player) || !movementReady || movementPlayer!=player
			|| !HostTerrain.movementReady()) return;
		long now=System.nanoTime();
		if (!tick && now-lastFramePublishNanos<16_000_000L) return;
		if (!tick) lastFramePublishNanos=now;
		lastStateNanos=now;
		JsonObject state=new JsonObject();state.addProperty("t","mcstate");
		state.addProperty("session",movementSession);state.addProperty("seq",++stateSequence);
		state.addProperty("tick",player.tickCount);state.addProperty("timeMs",System.currentTimeMillis());state.addProperty("tn",now);
		state.addProperty("ready",true);state.add("p",vector(player.position()));
		state.addProperty("render",!tick);
		state.add("eye",vector(player.getEyePosition()));
		if (!tick) {
			// Only real render frames carry interpolated positions. A tick's partial=1 would
			// otherwise jump the host camera forward before the next frame's partial~0.
			state.add("renderP",vector(player.getPosition(partialTick)));
			// Never use Camera.position(): that is the host camera and would create a feedback loop.
			state.add("renderEye",vector(player.getEyePosition(partialTick)));
		}
		state.addProperty("eyeHeight",player.getEyeHeight());state.add("v",vector(player.getDeltaMovement()));
		state.addProperty("grounded",player.onGround());state.addProperty("sneak",player.isShiftKeyDown());
		state.addProperty("sprint",player.isSprinting());state.addProperty("pose",player.getPose().name());
		state.addProperty("flying",player.getAbilities().flying);state.addProperty("fallFlying",player.isFallFlying());
		state.addProperty("passenger",player.isPassenger());state.addProperty("vehicleId",player.getVehicle()==null?-1:player.getVehicle().getId());
		state.addProperty("yaw",player.getYRot());state.addProperty("pitch",player.getXRot());
		Passthrough.events.accept(state.toString());
	}
	static void describeMovement(JsonObject state) {
		var request=HostPlayerBody.movement();
		state.addProperty("mcMoveRequested",request!=null);
		state.addProperty("mcMoveSession",request==null?0:request.session());
		state.addProperty("mcMoveConfirmedSession",movementSession);
		state.addProperty("mcMoveReady",request!=null && movementReady && request.session()==movementSession && HostTerrain.movementReady());
		state.addProperty("mcMoveStatus",request==null?"off":movementStatus);
		state.addProperty("mcMoveCamAgeMs",HostPlayerBody.movementAgeMillis());
		state.addProperty("mcMoveStateAgeMs",lastStateNanos==0?-1:(System.nanoTime()-lastStateNanos)/1_000_000L);
		state.addProperty("mcMoveSeq",stateSequence);state.addProperty("mcMoveGeometryReady",HostTerrain.movementReady());
	}

	private static void applyHostSneak(LocalPlayer player) {
		var host=HostPlayerBody.current();
		var key=(KeyMappingAccessor)Minecraft.getInstance().options.keyShift;
		if (host!=null) {
			// Set held state, not ToggleKeyMapping.setDown (which can toggle every tick).
			key.passthrough$setDown(host.sneak());
			hostSneakApplied=true;
		} else if (hostSneakApplied) {
			key.passthrough$setDown(false);
			hostSneakApplied=false;
		}
	}

	private static void applyHostPosture(LocalPlayer player) {
		var host=HostPlayerBody.current();
		if (host==null || player.isPassenger() || player.isSleeping()) return;
		player.setShiftKeyDown(host.sneak());
		player.setPose(host.sneak()?Pose.CROUCHING:Pose.STANDING);
		player.setOnGround(host.grounded());
	}

	/** Shift must reach vanilla interaction packets, but may not move the flying MC copy downward. */
	public static void beforeSend(LocalPlayer player) {
		var host=HostPlayerBody.current();
		if (host==null || player.isPassenger()) return;
		player.setPos(host.x(),host.y(),host.z());
		player.setDeltaMovement(Vec3.ZERO);
		applyHostPosture(player);
		Input input=player.input.keyPresses;
		player.input.keyPresses=new Input(input.forward(),input.backward(),input.left(),input.right(),input.jump(),host.sneak(),input.sprint());
	}
}
