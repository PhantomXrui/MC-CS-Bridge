package dev.rehan.passthrough;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import java.util.HashMap;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.entity.projectile.FireworkRocketEntity;
import net.minecraft.world.entity.projectile.Projectile;
import net.minecraft.world.entity.projectile.arrow.AbstractArrow;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.Vec3;

/** Server-side half: the host's collision as invisible barrier blocks, commands, and events back to the host. */
public final class WorldBridge {
	private static volatile MinecraftServer server;
	/** Barriers we placed (so a reset only removes ours, never the player's builds). */
	private static final Set<BlockPos> barriers = ConcurrentHashMap.newKeySet();
	/** Read shapes at the end of the tick, after neighbors and block entities have settled. */
	private static final Set<BlockPos> changes = new LinkedHashSet<>();
	private static final Set<BlockPos> moving = new LinkedHashSet<>();
	private static final Set<BlockPos> forceShapes = new LinkedHashSet<>();
	private static final Set<BlockPos> priorityShapes = new LinkedHashSet<>();
	private static final Map<BlockPos, String> sentShapes = new HashMap<>();
	private static final Map<BlockPos, String> sentMaterials = new HashMap<>();
	private static final int MAX_BOXES_PER_CELL = 32, CELLS_PER_MESSAGE = 128, CELLS_PER_TICK = 512;
	private static final int MAX_MESSAGE_CHARS = 48 * 1024;
	private static final int SYNC_SCAN_PER_TICK = 4096;
	private static SyncScan syncScan;
	private static long syncSerial;
	private static boolean warnedComplexShape;
	/** While placing or removing the host's own ground: those changes aren't news to the host (server thread only). */
	private static boolean placingGround;

	private WorldBridge() {
	}

	static void attach(final MinecraftServer s) {
		server = s;
	}

	static void detach() {
		server = null;
		barriers.clear();
		changes.clear();
		moving.clear();
		forceShapes.clear();
		priorityShapes.clear();
		sentShapes.clear();
		sentMaterials.clear();
		syncScan = null;
	}

	public static boolean ready() {
		return server != null;
	}

	/** Host terrain is opaque support, not a player-waterloggable Minecraft barrier. */
	public static boolean isHostBarrier(final BlockPos pos) {
		return barriers.contains(pos);
	}

	/** CS reserves barrier blocks as invisible support, including legacy cells loaded from disk.
	 * Keep ownership tracking separate: it must not decide rendering or projectile collision. */
	public static boolean isHostSupport(final BlockState state, final BlockPos pos) {
		return state.is(Blocks.BARRIER) && ((MobWar.VANILLA_COMBAT ? HostTerrain.movementReady() : HostTerrain.enabled()) || isHostBarrier(pos));
	}

	static MinecraftServer server() {
		return server;
	}

	/** Columns of solid ground from the host: {x, z, yBottom, yTop, ...} in block coordinates (inclusive). Only air is replaced. */
	public static void solid(final int[] columns) {
		MinecraftServer s = server;
		if (s == null) {
			return;
		}

		s.execute(() -> {
			ServerLevel level = s.overworld();
			BlockState barrier = Blocks.BARRIER.defaultBlockState();
			BlockPos.MutableBlockPos pos = new BlockPos.MutableBlockPos();
			placingGround = true;
			for (int i = 0; i + 3 < columns.length; i += 4) {
				for (int y = columns[i + 2]; y <= columns[i + 3]; y++) {
					pos.set(columns[i], y, columns[i + 1]);
					if (level.isInWorldBounds(pos)) {
						BlockState current = level.getBlockState(pos);
						if (current.isAir()) {
							level.setBlock(pos, barrier, Block.UPDATE_CLIENTS | Block.UPDATE_KNOWN_SHAPE);
							barriers.add(pos.immutable());
						} else if (current.is(Blocks.BARRIER)) {
							// Reclaim host terrain persisted by a previous integrated-server session.
							barriers.add(pos.immutable());
						}
					}
				}
			}

			placingGround = false;
		});
	}

	/** Remove every barrier we placed (e.g. when the host teleports somewhere else). */
	public static void clearSolid() {
		MinecraftServer s = server;
		if (s == null) {
			return;
		}

		s.execute(() -> {
			ServerLevel level = s.overworld();
			placingGround = true;
			for (BlockPos pos : barriers) {
				if (level.getBlockState(pos).is(Blocks.BARRIER)) {
					level.setBlock(pos, Blocks.AIR.defaultBlockState(), Block.UPDATE_CLIENTS | Block.UPDATE_KNOWN_SHAPE);
				}
			}

			placingGround = false;
			barriers.clear();
		});
	}

	/** Run a command as the server (op). Results go to the log, not to chat (send_command_feedback is off). */
	public static void command(final String command) {
		MinecraftServer s = server;
		if (s == null) {
			return;
		}

		s.execute(() -> {
			Passthrough.LOG.info("command: {}", command);
			s.getCommands().performPrefixedCommand(s.createCommandSourceStack(), command);
		});
	}

	/** Arrows the host already hit something with (they stay where they hit and aren't reported again). */
	private static final String HIT_TAG = "passthrough_hit";

	/** Every server tick: block changes, and projectiles in flight for the host to trace through its own world. */
	static void tick(final MinecraftServer s) {
		if (Passthrough.active && !HostPlayerBody.minecraftMovement()) applyPlayerBlockContacts(s);
		advanceSync(s.overworld());
		flush(s);
		finishSync();
		if (Passthrough.active) {
			reportProjectiles(s.overworld());
		}

		MobWar.tick(s);
		Nether.tick(s);
	}

	private static void applyPlayerBlockContacts(MinecraftServer s) {
		for (ServerPlayer player : s.getPlayerList().getPlayers()) {
			if (player.level() != s.overworld() || !player.isAlive() || player.isSpectator()) continue;
			// Host-following sets noPhysics, which makes vanilla isAffectedByBlocks return false.
			// Run contact effects once at the current body, without enabling movement collisions
			// or sweeping a teleport through every intervening pressure plate/tripwire.
			boolean noPhysics = player.noPhysics;
			player.noPhysics = false;
			try { player.applyEffectsFromBlocks(player.position(), player.position()); }
			finally { player.noPhysics = noPhysics; }
		}
	}

	/**
	 * Steve's arrows (bow, crossbow) and crossbow fireworks in flight: {"t":"proj","p":[[id,kind,x,y,z],...]}. Mobs'
	 * arrows aren't traced by the host: they hit its people's proxies here.
	 */
	private static void reportProjectiles(final ServerLevel level) {
		StringBuilder b = null;
		for (Entity e : level.getAllEntities()) {
			String kind = null;
			if (!(e instanceof Projectile projectile) || !(projectile.getOwner() instanceof Player)) {
				continue;
			}

			if (e instanceof AbstractArrow arrow && !arrow.entityTags().contains(HIT_TAG) && arrow.getDeltaMovement().lengthSqr() > 1.0E-4) {
				if (HostTerrain.enabled()) continue; // Synchronous vanilla arrow collision now includes exact host terrain.
				kind = "arrow";
			} else if (e instanceof FireworkRocketEntity rocket && rocket.isShotAtAngle()) {
				kind = "firework"; // not the ones boosting an elytra flight
			}

			if (kind != null) {
				b = b == null ? new StringBuilder("{\"t\":\"proj\",\"p\":[") : b.append(',');
				b.append(String.format(Locale.ROOT, "[%d,\"%s\",%.3f,%.3f,%.3f]", e.getId(), kind, e.getX(), e.getY(), e.getZ()));
			}
		}

		if (b != null) {
			Passthrough.events.accept(b.append("]}").toString());
		}
	}

	/**
	 * The host traced a projectile into something of its own: a firework bursts there; an arrow goes into a person
	 * or car (gone) or sticks where it hit a wall.
	 */
	public static void projectileHit(final int id, final double x, final double y, final double z, final boolean stick) {
		MinecraftServer s = server;
		if (s == null) {
			return;
		}

		s.execute(() -> {
			ServerLevel level = s.overworld();
			Entity e = level.getEntity(id);
			if (e instanceof FireworkRocketEntity) {
				e.setPos(x, y, z);
				level.broadcastEntityEvent(e, (byte) 17);
				e.discard();
			} else if (e instanceof AbstractArrow) {
				if (stick) {
					e.setPos(x, y, z);
					e.setDeltaMovement(Vec3.ZERO);
					e.setNoGravity(true);
					e.addTag(HIT_TAG);
				} else {
					e.discard();
				}
			}
		});
	}

	/** Server thread, from Level.setBlock: remember the change; flushed once per tick. */
	public static void onBlockChanged(final ServerLevel level, final BlockPos pos, final BlockState state) {
		if (!Passthrough.active || level != level.getServer().overworld()) {
			return;
		}

		Nether.onBlockChanged(pos, state);
		if (!placingGround) {
			changes.add(pos.immutable());
			if (state.is(Blocks.MOVING_PISTON)) moving.add(pos.immutable());
			// Fences, walls and other connected shapes also depend on neighboring states.
			for (Direction direction : Direction.values()) changes.add(pos.relative(direction));
		}
	}

	/** While on, block changes are the host's own ground being edited (not reported as blocks to collide with). */
	static void quietGround(final boolean on) {
		placingGround = on;
	}

	/** Existing set/clear remains compatible; shapes contains authoritative per-cell local AABBs.
	 * A shapes row is [x,y,z,minX,minY,minZ,maxX,maxY,maxZ,...], or [x,y,z] to clear.
	 * Matching materials rows [x,y,z,"minecraft:block_id"] include noncolliding blocks and air removals.
	 * Piston boxes can extend beyond the unit cell. Never clamp them to [0,1]. */
	static void flush(final MinecraftServer s) {
		ServerLevel level = s.overworld();
		Set<BlockPos> batch = new LinkedHashSet<>();
		Iterator<BlockPos> priority = priorityShapes.iterator();
		while (priority.hasNext() && batch.size() < CELLS_PER_TICK / 2) {
			BlockPos pos = priority.next(); batch.add(pos); changes.remove(pos); priority.remove();
		}
		// A moving piston changes its collision every tick without a setBlock call.
		Iterator<BlockPos> motion = moving.iterator();
		while (motion.hasNext() && batch.size() < CELLS_PER_TICK / 2) {
			BlockPos pos = motion.next();
			if (!level.isLoaded(pos) || !level.getBlockState(pos).is(Blocks.MOVING_PISTON)) motion.remove();
			batch.add(pos);
		}
		Iterator<BlockPos> pending = changes.iterator();
		while (pending.hasNext() && batch.size() < CELLS_PER_TICK) {
			batch.add(pending.next());
			pending.remove();
		}
		JsonArray set = new JsonArray(), clear = new JsonArray(), shapes = new JsonArray(), materials = new JsonArray();
		int messageChars = 100;
		for (BlockPos pos : batch) {
			if (syncScan != null) syncScan.pending.remove(pos);
			if (!level.isInWorldBounds(pos) || !level.isLoaded(pos)) continue;
			BlockState state = level.getBlockState(pos);
			List<AABB> boxes = collisionBoxes(level, pos, state);
			JsonArray row = shapeRow(pos, boxes);
			String signature = row.toString();
			String material = BuiltInRegistries.BLOCK.getKey(state.getBlock()).toString();
			boolean force = forceShapes.remove(pos);
			boolean sameShape = boxes.isEmpty() ? !sentShapes.containsKey(pos) : signature.equals(sentShapes.get(pos));
			if (!force && sameShape && material.equals(sentMaterials.getOrDefault(pos,"minecraft:air"))) continue;
			if (boxes.isEmpty()) sentShapes.remove(pos); else sentShapes.put(pos, signature);
			if (state.isAir()) sentMaterials.remove(pos); else sentMaterials.put(pos,material);
			JsonArray materialRow = shapeRow(pos,List.of()); materialRow.add(material);
			int rowChars = signature.length() + materialRow.toString().length() + 40;
			if (!shapes.isEmpty() && messageChars + rowChars > MAX_MESSAGE_CHARS) {
				emitShapes(set, clear, shapes, materials);
				set = new JsonArray(); clear = new JsonArray(); shapes = new JsonArray(); materials = new JsonArray(); messageChars = 100;
			}
			JsonArray coordinates = boxes.isEmpty() ? clear : set;
			coordinates.add(pos.getX()); coordinates.add(pos.getY()); coordinates.add(pos.getZ());
			shapes.add(row);
			materials.add(materialRow);
			messageChars += rowChars;
			if (shapes.size() >= CELLS_PER_MESSAGE) {
				emitShapes(set, clear, shapes, materials);
				set = new JsonArray(); clear = new JsonArray(); shapes = new JsonArray(); materials = new JsonArray(); messageChars = 100;
			}
		}
		if (!shapes.isEmpty()) emitShapes(set, clear, shapes, materials);
	}

	private static List<AABB> collisionBoxes(ServerLevel level, BlockPos pos, BlockState state) {
		if (state.isAir() || state.is(Blocks.BARRIER) || Nether.isGround(pos)) return List.of();
		var shape = state.getCollisionShape(level, pos);
		List<AABB> boxes = shape.toAabbs();
		if (boxes.size() <= MAX_BOXES_PER_CELL) return boxes;
		if (!warnedComplexShape) {
			warnedComplexShape = true;
			Passthrough.LOG.warn("Collision shape exceeds {} boxes; using conservative bounds for complex cells", MAX_BOXES_PER_CELL);
		}
		return List.of(shape.bounds());
	}

	private static JsonArray shapeRow(BlockPos pos, List<AABB> boxes) {
		JsonArray row = new JsonArray();
		row.add(pos.getX()); row.add(pos.getY()); row.add(pos.getZ());
		for (AABB box : boxes) {
			row.add(box.minX); row.add(box.minY); row.add(box.minZ);
			row.add(box.maxX); row.add(box.maxY); row.add(box.maxZ);
		}
		return row;
	}

	private static void emitShapes(JsonArray set, JsonArray clear, JsonArray shapes, JsonArray materials) {
		JsonObject message = new JsonObject(); message.addProperty("t", "blocks");
		message.add("set", set); message.add("clear", clear); message.add("shapes", shapes); message.add("materials",materials);
		Passthrough.events.accept(message.toString());
	}

	/** Snapshot near the player, incrementally scanned so a large redstone build cannot stall one server tick. */
	public static void sync(final int radius) {
		sync(radius, 0);
	}

	/** The host correlates blocksbegin/blocksready by request ID while briefly holding its player in place. */
	public static void sync(final int radius, final long requestId) {
		sync(radius, requestId, null);
	}

	/** Explicit host feet avoid a stale MC pose after a round change or teleport. */
	public static void sync(final int radius, final long requestId, final int[] hostCenter) {
		MinecraftServer s = server;
		if (s == null) {
			return;
		}

		s.execute(() -> {
			ServerLevel level = s.overworld();
			ServerPlayer player = s.getPlayerList().getPlayers().isEmpty() ? null : s.getPlayerList().getPlayers().get(0);
			if (player == null) {
				return;
			}

			if (syncScan != null) {
				JsonObject cancelled = new JsonObject(); cancelled.addProperty("t","blockscancel");
				cancelled.addProperty("id",syncScan.id); cancelled.addProperty("reason","superseded");
				Passthrough.events.accept(cancelled.toString());
			}
			BlockPos center = hostCenter != null && hostCenter.length == 3 ? new BlockPos(hostCenter[0],hostCenter[1],hostCenter[2]) : player.blockPosition();
			SyncScan scan = new SyncScan(center, Math.clamp(radius, 1, 64), requestId != 0 ? requestId : ++syncSerial);
			syncScan = scan;
			JsonObject begin = new JsonObject(); begin.addProperty("t","blocksbegin"); begin.addProperty("id",scan.id);
			begin.add("center",shapeRow(scan.center,List.of())); begin.addProperty("r",scan.radius);
			Passthrough.events.accept(begin.toString());
			// Restore the cells at/under the feet in the first outgoing batch, before the wider scan.
			for (int y=-1;y<=1;y++) for (int x=-1;x<=1;x++) for (int z=-1;z<=1;z++) {
				BlockPos pos = scan.center.offset(x,y,z);
				if (level.isInWorldBounds(pos) && level.isLoaded(pos)) queueSyncCell(scan, pos, level.getBlockState(pos), true);
			}
		});
	}

	private static final class SyncScan {
		final BlockPos center;
		final int radius, width;
		final long id;
		final List<int[]> columns = new ArrayList<>();
		final Set<BlockPos> pending = new LinkedHashSet<>(), counted = new LinkedHashSet<>();
		int cursor, found;
		boolean scanned, limited;
		SyncScan(BlockPos center, int radius, long id) {
			this.center=center; this.radius=radius; this.width=radius*2+1; this.id=id;
			// Concentric square rings: no sort, no per-tick full-map work, nearest columns first.
			columns.add(new int[]{0,0});
			for (int ring=1;ring<=radius;ring++) {
				for(int x=-ring;x<=ring;x++){columns.add(new int[]{x,-ring});columns.add(new int[]{x,ring});}
				for(int z=-ring+1;z<ring;z++){columns.add(new int[]{-ring,z});columns.add(new int[]{ring,z});}
			}
		}
	}

	private static void queueSyncCell(SyncScan scan, BlockPos pos, BlockState state, boolean priority) {
		BlockPos key = pos.immutable();
		forceShapes.add(key); changes.add(key); scan.pending.add(key);
		if (priority) priorityShapes.add(key);
		if (!state.isAir() && !state.is(Blocks.BARRIER) && !Nether.isGround(pos) && scan.counted.add(key)) scan.found++;
		if (state.is(Blocks.MOVING_PISTON)) moving.add(key);
	}

	private static void advanceSync(ServerLevel level) {
		SyncScan scan = syncScan;
		if (scan == null || scan.scanned) return;
		BlockPos.MutableBlockPos pos = new BlockPos.MutableBlockPos();
		int end = Math.min(scan.width * scan.width * 65, scan.cursor + SYNC_SCAN_PER_TICK);
		for (; scan.cursor < end && scan.found < 3000; scan.cursor++) {
			int[] column = scan.columns.get(scan.cursor / 65);
			pos.set(scan.center.getX()+column[0], scan.center.getY()+scan.cursor%65-24, scan.center.getZ()+column[1]);
			if (!level.isInWorldBounds(pos) || !level.isLoaded(pos)) continue;
			BlockState state = level.getBlockState(pos);
			if (!state.isAir() && !state.is(Blocks.BARRIER) && !Nether.isGround(pos)) {
				queueSyncCell(scan,pos,state,false);
			} else if (sentShapes.containsKey(pos) || sentMaterials.containsKey(pos)) queueSyncCell(scan,pos,state,false);
		}
		if (scan.cursor >= scan.width * scan.width * 65 || scan.found >= 3000) {
			scan.scanned = true;
			scan.limited = scan.cursor < scan.width * scan.width * 65;
		}
	}

	private static void finishSync() {
		SyncScan scan = syncScan;
		if (scan == null || !scan.scanned || !scan.pending.isEmpty()) return;
		JsonObject ready = new JsonObject(); ready.addProperty("t","blocksready"); ready.addProperty("id",scan.id);
		ready.addProperty("found",scan.found); ready.addProperty("scanned",scan.cursor); ready.addProperty("limited",scan.limited);
		// This event follows all shapes in the same ordered transport, never just the end of scanning.
		Passthrough.events.accept(ready.toString());
		Passthrough.LOG.info("Collision sync {} finished: {} cells, {} blocks, limited={}",scan.id,scan.cursor,scan.found,scan.limited);
		syncScan = null;
	}

	/** Read-only redstone diagnostic, bounded to 64 supplied cells and executed on the server thread. */
	public static void inspect(final int[] positions) {
		MinecraftServer s = server;
		if (s == null) return;
		s.execute(() -> {
			ServerLevel level = s.overworld();
			JsonObject message = new JsonObject(); message.addProperty("t", "blockinspect");
			message.addProperty("tick", level.getGameTime());
			JsonArray cells = new JsonArray();
			for (int i=0; i+2<positions.length && i<64*3; i+=3) {
				BlockPos pos = new BlockPos(positions[i],positions[i+1],positions[i+2]);
				JsonObject cell = new JsonObject();
				cell.add("pos", shapeRow(pos,List.of()));
				boolean loaded = level.isInWorldBounds(pos) && level.isLoaded(pos);
				cell.addProperty("loaded",loaded);
				if (loaded) {
					BlockState state = level.getBlockState(pos);
					cell.addProperty("state",state.toString());
					cell.addProperty("neighborSignal",level.getBestNeighborSignal(pos));
					cell.addProperty("signalSource",state.isSignalSource());
					cell.addProperty("redstoneConductor",state.isRedstoneConductor(level,pos));
					cell.addProperty("hostSupport",isHostSupport(state,pos));
					cell.addProperty("moving",state.is(Blocks.MOVING_PISTON));
					cell.add("shape",shapeRow(pos,collisionBoxes(level,pos,state)));
				}
				cells.add(cell);
			}
			message.add("cells",cells);
			Passthrough.events.accept(message.toString());
		});
	}

	/** Elytra on and gliding (creative flight off), launched forward along the look; or back to creative flight. */
	public static void glide(final boolean start, final double speed) {
		MinecraftServer s = server;
		if (s == null) {
			return;
		}

		s.execute(() -> {
			if (s.getPlayerList().getPlayers().isEmpty()) {
				return;
			}

			ServerPlayer player = s.getPlayerList().getPlayers().get(0);
			if (start) {
				player.setItemSlot(EquipmentSlot.CHEST, new ItemStack(Items.ELYTRA));
				player.getAbilities().flying = false;
				player.onUpdateAbilities();
				player.setOnGround(false);
				player.startFallFlying();
				player.setDeltaMovement(player.getLookAngle().scale(speed));
				player.needsSync = true;
			} else {
				player.stopFallFlying();
				player.setItemSlot(EquipmentSlot.CHEST, ItemStack.EMPTY);
				player.getAbilities().flying = true;
				player.onUpdateAbilities();
			}
		});
	}

	/** `source`: what exploded or was blown up, e.g. "tnt", "creeper", "fireball" (a ghast's). */
	public static void onExplosion(final Vec3 center, final float radius, final String source) {
		if (Passthrough.active) {
			Passthrough.events.accept(String.format(Locale.ROOT, "{\"t\":\"explosion\",\"pos\":[%.3f,%.3f,%.3f],\"r\":%.2f,\"src\":\"%s\"}",
				center.x, center.y, center.z, radius, source));
		}
	}
}
