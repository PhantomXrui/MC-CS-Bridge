package dev.rehan.passthrough.client;

import com.google.gson.JsonObject;
import dev.rehan.passthrough.Passthrough;
import dev.rehan.passthrough.HostPlayerBody;
import dev.rehan.passthrough.client.mixin.KeyMappingAccessor;
import net.minecraft.client.KeyMapping;
import net.minecraft.client.Minecraft;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.world.entity.player.Inventory;
import org.lwjgl.sdl.SDLVideo;
import net.minecraft.client.input.MouseButtonEvent;
import net.minecraft.client.input.MouseButtonInfo;
import net.minecraft.client.input.KeyEvent;
import net.minecraft.client.input.CharacterEvent;

/** Host input, applied on the client thread: the host window has the focus, so Minecraft never sees these itself. */
final class ClientInput {
	private static final com.google.gson.Gson DIAGNOSTIC_JSON=new com.google.gson.Gson();
	private static int lastButtons;
	private static double lastX, lastY;
	private static long controlNanos;
	private static long movementSession;
	private static int movementHeld, movementPressed;
	private static KeyMapping[] movementKeys(Minecraft minecraft) {
		return new KeyMapping[]{minecraft.options.keyUp,minecraft.options.keyDown,minecraft.options.keyLeft,minecraft.options.keyRight,
			minecraft.options.keyJump,minecraft.options.keyShift,minecraft.options.keySprint};
	}
	static void beginMovement(Minecraft minecraft,long session) {
		if (movementSession!=session) {resetMovement(minecraft);movementSession=session;}
	}
	static void resetMovement(Minecraft minecraft) {
		movementHeld=0;movementPressed=0;movementSession=0;
		for (KeyMapping key:movementKeys(minecraft)) {
			((KeyMappingAccessor)key).passthrough$setDown(false);
			((KeyMappingAccessor)key).passthrough$setClickCount(0);
		}
	}
	static void applyMovement(Minecraft minecraft,boolean ready) {
		boolean enabled=ready && minecraft.gui.screen()==null;
		KeyMapping[] keys=movementKeys(minecraft);
		for(int i=0;i<keys.length;i++) ((KeyMappingAccessor)keys[i]).passthrough$setDown(enabled && ((movementHeld|movementPressed)&(1<<i))!=0);
	}
	static void endMovementTick() {movementPressed=0;}
	private static void movement(Minecraft minecraft,JsonObject message) {
		var request=HostPlayerBody.movement();
		if(request==null) return;
		long session=message.has("session")?message.get("session").getAsLong():request.session();
		if(session!=request.session()) return;
		int bit=switch(message.get("k").getAsString()) {
			case "forward"->1;case "back"->2;case "left"->4;case "right"->8;case "jump"->16;case "sneak"->32;case "sprint"->64;default->0;
		};
		if(bit==0) return;
		beginMovement(minecraft,session);
		boolean down=!message.has("down")||message.get("down").getAsBoolean();
		if(down) {
			// A down/up pair delivered between MC ticks still reaches vanilla input for one tick.
			if((movementHeld&bit)==0) movementPressed|=bit;
			movementHeld|=bit;
		} else movementHeld&=~bit;
	}
	static void reset(final Minecraft minecraft, boolean closeScreen) {
		resetMovement(minecraft);
		for (KeyMapping key : new KeyMapping[]{minecraft.options.keyUse,minecraft.options.keyAttack,minecraft.options.keyInventory,minecraft.options.keyDrop,minecraft.options.keySwapOffhand}) {
			key.setDown(false);
			((KeyMappingAccessor)key).passthrough$setClickCount(0);
		}
		((KeyMappingAccessor)minecraft.options.keyShift).passthrough$setDown(false);
		var screen=minecraft.gui.screen();
		if(screen!=null) {
			for(int i=0;i<2;i++) if((lastButtons&(1<<i))!=0) screen.mouseReleased(new MouseButtonEvent(lastX,lastY,new MouseButtonInfo(i==0?1:3,0)));
			if(closeScreen) screen.onClose();
		}
		lastButtons=0;
	}
	static void tick(final Minecraft minecraft) {
		if(controlNanos!=0 && System.nanoTime()-controlNanos>1_000_000_000L) {reset(minecraft,true);controlNanos=0;}
		if(!HostPlayerBody.minecraftMovement() && movementSession!=0) resetMovement(minecraft);
		boolean blocking=Passthrough.active && minecraft.gui.screen()==null && minecraft.player!=null && minecraft.player.isBlocking();
		Passthrough.events.accept("{\"t\":\"shieldstate\",\"blocking\":"+blocking+"}");
	}
	private ClientInput() {
	}

	static void handle(final Minecraft minecraft, final JsonObject m) {
		LocalPlayer player = minecraft.player;
		switch (m.get("t").getAsString()) {
			case "move" -> movement(minecraft,m);
			case "movement_off" -> {
				var request=HostPlayerBody.movement();
				long session=m.has("session")?m.get("session").getAsLong():0;
				if(request!=null && request.session()==session) {HostPlayerBody.stopMovement(session);resetMovement(minecraft);}
			}
			case "frame_budget" -> minecraft.options.framerateLimit().set(Math.clamp(m.get("fps").getAsInt(),30,120));
			case "render_pose" -> HostRenderPose.enabled=m.get("on").getAsBoolean();
			case "shieldhit" -> { if(player!=null) player.playSound(net.minecraft.sounds.SoundEvents.SHIELD_BLOCK.value(),0.8F,1.0F); }
			case "control" -> {
				controlNanos=System.nanoTime();
				if(!m.get("active").getAsBoolean()) reset(minecraft,m.has("close")&&m.get("close").getAsBoolean());
			}
			case "resetinput" -> reset(minecraft,!m.has("close")||m.get("close").getAsBoolean());
			case "uitext" -> {
				var screen=minecraft.gui.screen();
				if(screen!=null) m.get("text").getAsString().codePoints().limit(256).filter(c->c>=32&&c!=127).forEach(c->screen.charTyped(new CharacterEvent(c)));
			}
			case "uikey" -> {
				var screen=minecraft.gui.screen();
				if(screen!=null) {
					var event=new KeyEvent(m.get("key").getAsInt(),m.get("code").getAsInt(),m.get("mods").getAsInt());
					if(m.get("down").getAsBoolean()) screen.keyPressed(event); else screen.keyReleased(event);
				}
			}
			case "uistate" -> {
				var screen=minecraft.gui.screen();
				JsonObject state=new JsonObject();state.addProperty("t","uistate");
				var integrated=minecraft.getSingleplayerServer();
				boolean worldLoaded=integrated!=null && integrated.isRunning() && !integrated.isStopped() && minecraft.level!=null && player!=null;
				String saveName="";
				if(worldLoaded) {
					// ROOT is "." in 26.3: normalize before taking the actual save directory name.
					var saveRoot=integrated.getWorldPath(net.minecraft.world.level.storage.LevelResource.ROOT).toAbsolutePath().normalize();
					if(saveRoot.getFileName()!=null) saveName=saveRoot.getFileName().toString();
				}
				state.addProperty("worldLoaded",worldLoaded);state.addProperty("saveName",saveName);
				PlayerSync.describeMovement(state);
				state.add("mcTerrain",DIAGNOSTIC_JSON.toJsonTree(dev.rehan.passthrough.HostTerrain.diagnostics()));
				state.addProperty("mcMoveHeld",movementHeld);state.addProperty("mcMovePressed",movementPressed);
				if(m.has("requestId") && m.get("requestId").isJsonPrimitive() && m.getAsJsonPrimitive("requestId").isString()) {
					String requestId=m.get("requestId").getAsString();
					if(requestId.length()<=64) state.addProperty("requestId",requestId);
				}
				state.addProperty("screen",screen==null?"":screen.getClass().getSimpleName());
				state.addProperty("buttons",lastButtons);state.addProperty("attack",minecraft.options.keyAttack.isDown());
				state.addProperty("use",minecraft.options.keyUse.isDown());
				state.addProperty("renderPoseShared",HostRenderPose.used);
				state.addProperty("fpsLimit",minecraft.options.framerateLimit().get());
				state.addProperty("frameWidth",minecraft.getWindow().getWidth());
				state.addProperty("frameHeight",minecraft.getWindow().getHeight());
				state.addProperty("cameraType",minecraft.options.getCameraType().name());
				var host=HostState.frame();
				if(host!=null){state.addProperty("hostCamera",host.x()+","+host.y()+","+host.z());state.addProperty("hostYaw",host.yaw());state.addProperty("hostPitch",host.pitch());state.addProperty("lookYaw",host.lookYaw());state.addProperty("lookPitch",host.lookPitch());}
				if(player!=null) {
					state.addProperty("slot",player.getInventory().getSelectedSlot());
					state.addProperty("carried",BuiltInRegistries.ITEM.getKey(player.containerMenu.getCarried().getItem()).toString());
					state.addProperty("held",BuiltInRegistries.ITEM.getKey(player.getMainHandItem().getItem()).toString());
					state.addProperty("usingItem",player.isUsingItem());
					state.addProperty("blocking",player.isBlocking());
					state.addProperty("health",player.getHealth());state.addProperty("food",player.getFoodData().getFoodLevel());state.addProperty("absorption",player.getAbsorptionAmount());state.addProperty("vitalsHud",dev.rehan.passthrough.HostVitals.enabled());
					state.addProperty("offhand",BuiltInRegistries.ITEM.getKey(player.getOffhandItem().getItem()).toString());
					state.addProperty("useTicks",player.getTicksUsingItem());
					state.addProperty("hit",minecraft.hitResult==null?"null":minecraft.hitResult.toString());
					if (minecraft.hitResult instanceof net.minecraft.world.phys.BlockHitResult hit && minecraft.level!=null) {
						var pos=hit.getBlockPos();var block=minecraft.level.getBlockState(pos);
						state.addProperty("hitType",hit.getType().name());
						state.addProperty("hitBlock",BuiltInRegistries.BLOCK.getKey(block.getBlock()).toString());
						state.addProperty("hitBlockPos",pos.getX()+","+pos.getY()+","+pos.getZ());
						state.addProperty("hitTrackedSupport",dev.rehan.passthrough.WorldBridge.isHostBarrier(pos));
						state.addProperty("hideSupportOutline",Passthrough.active && block.is(net.minecraft.world.level.block.Blocks.BARRIER));
					}
					state.addProperty("x",player.getX());state.addProperty("y",player.getY());state.addProperty("z",player.getZ());
					state.addProperty("sneak",player.isShiftKeyDown());state.addProperty("crouching",player.isCrouching());
					state.addProperty("pose",player.getPose().name());state.addProperty("grounded",player.onGround());
					state.addProperty("mayfly",player.getAbilities().mayfly);state.addProperty("flying",player.getAbilities().flying);
					state.addProperty("fallFlying",player.isFallFlying());state.addProperty("sprinting",player.isSprinting());
					state.addProperty("eyeHeight",player.getEyeHeight());state.addProperty("eyeY",player.getEyeY());
					var body=dev.rehan.passthrough.HostPlayerBody.current();
					if(body!=null) {state.addProperty("hostFeet",body.x()+","+body.y()+","+body.z());state.addProperty("hostBody",body.bounds().toString());}
				}
				if(screen!=null) {state.addProperty("width",screen.width);state.addProperty("height",screen.height);
					for(var child:screen.children()) if(child instanceof net.minecraft.client.gui.components.EditBox box && box.isVisible()) state.addProperty("search",box.getValue());
				}
				Passthrough.events.accept(state.toString());
			}
			case "pointer" -> {
				var screen = minecraft.gui.screen();
				if (screen == null) { lastButtons = 0; return; }
				double x = m.get("x").getAsDouble() * screen.width;
				double y = m.get("y").getAsDouble() * screen.height;
				int buttons = m.get("buttons").getAsInt();
				int modifiers = m.has("shift") && m.get("shift").getAsBoolean() ? 1 : 0;
				minecraft.mouseHandler.onMove(minecraft.getWindow().handle(), m.get("x").getAsDouble() * minecraft.getWindow().getWidth(), m.get("y").getAsDouble() * minecraft.getWindow().getHeight(), 0, 0);
				for (int i=0;i<2;i++) {
					var event = new MouseButtonEvent(x,y,new MouseButtonInfo(i==0?1:3,modifiers));
					if ((buttons & (1<<i)) != (lastButtons & (1<<i))) {
						if ((buttons & (1<<i)) != 0) screen.mouseClicked(event,false); else screen.mouseReleased(event);
					} else if ((buttons & (1<<i)) != 0) screen.mouseDragged(event,x-lastX,y-lastY);
				}
				if (m.has("wheel") && m.get("wheel").getAsInt()!=0) screen.mouseScrolled(x,y,0,m.get("wheel").getAsInt());
				lastButtons=buttons;lastX=x;lastY=y;
			}
			case "key" -> {
				String k = m.get("k").getAsString();
				boolean down = !m.has("down") || m.get("down").getAsBoolean();
				if (k.equals("escape")) {
					if (down && minecraft.gui.screen() != null) {
						minecraft.gui.screen().onClose();
					}

					return;
				}
				if (k.equals("inventory") && down && minecraft.gui.screen()!=null) { minecraft.gui.screen().onClose(); return; }
				if (k.equals("sneak")) { ((KeyMappingAccessor)minecraft.options.keyShift).passthrough$setDown(down); return; }

				KeyMapping key = switch (k) {
					case "use" -> minecraft.options.keyUse;
					case "attack" -> minecraft.options.keyAttack;
					case "pick" -> minecraft.options.keyPickItem;
					case "inventory" -> minecraft.options.keyInventory;
					case "drop" -> minecraft.options.keyDrop;
					case "swap" -> minecraft.options.keySwapOffhand;
					default -> null;
				};
				if (!dev.rehan.passthrough.MobWar.VANILLA_COMBAT && k.equals("attack") && down && player != null
					&& BuiltInRegistries.ITEM.getKey(player.getMainHandItem().getItem()).getPath().endsWith("_sword")) {
					// a sword swing: the host hits what's in front of Steve in its own world
					Passthrough.events.accept("{\"t\":\"melee\"}");
				}

				if (key != null) {
					if (down && !key.isDown()) {
						KeyMappingAccessor access = (KeyMappingAccessor)key;
						access.passthrough$setClickCount(access.passthrough$getClickCount() + 1);
					}

					key.setDown(down);
				}
			}
			case "slot" -> {
				if (player != null) {
					player.getInventory().setSelectedSlot(Math.clamp(m.get("n").getAsInt(), 0, Inventory.getSelectionSize() - 1));
				}
			}
			case "scroll" -> {
				if (player != null) {
					Inventory inventory = player.getInventory();
					int size = Inventory.getSelectionSize();
					inventory.setSelectedSlot(Math.floorMod(inventory.getSelectedSlot() - m.get("d").getAsInt(), size));
				}
			}
			case "hud" -> {
				if (minecraft.gui.hud.isHidden() != m.get("hidden").getAsBoolean()) {
					minecraft.gui.hud.toggle();
				}
			}
			case "view" -> {
				// match the host's picture exactly: un-minimize/un-maximize first (resizing a maximized window is ignored)
				int w = m.get("w").getAsInt(), h = m.get("h").getAsInt();
				long handle = minecraft.getWindow().handle();
				SDLVideo.SDL_RestoreWindow(handle);
				minecraft.getWindow().setWindowed(w, h);
				// A decorated desktop window is clamped to the work area (e.g. 2560x1494
				// for a 2560x1600 host). Match the actual client surface, not outer bounds.
				SDLVideo.SDL_SetWindowBordered(handle, false);
				SDLVideo.SDL_SetWindowPosition(handle, 0, 0);
				SDLVideo.SDL_SetWindowSize(handle, w, h);
				SDLVideo.SDL_SyncWindow(handle);
			}
			default -> {
			}
		}
	}
}
