package dev.rehan.passthrough;

import com.google.gson.JsonObject;
import dev.rehan.passthrough.mixin.MobAccessor;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Optional;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.atomic.AtomicReference;
import net.minecraft.core.BlockPos;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.resources.Identifier;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.tags.DamageTypeTags;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.effect.MobEffectInstance;
import net.minecraft.world.effect.MobEffects;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EntitySpawnReason;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.EntityTypes;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.boss.enderdragon.EnderDragon;
import net.minecraft.world.entity.boss.enderdragon.phases.EnderDragonPhase;
import net.minecraft.world.entity.ai.attributes.AttributeInstance;
import net.minecraft.world.entity.ai.attributes.Attributes;
import net.minecraft.world.entity.ai.goal.GoalSelector;
import net.minecraft.world.entity.monster.Enemy;
import net.minecraft.world.entity.npc.villager.Villager;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.entity.projectile.Projectile;

/**
 * Minecraft's mobs vs the host's people. The host lists its people ("peds"); each gets an invisible, AI-less
 * villager "proxy" here that follows it and that hostile mobs hunt. A mob's hit on a proxy goes back to the host
 * ("mobhit"), which hurts the real person. The other way round, the host keeps a stand-in for every mob ("mobs") that
 * its police shoot at, and sends the damage back ("mobdmg").
 */
public final class MobWar {
	/** CS adapter opts into vanilla combat. GTA's original event contract stays available. */
	public static final boolean VANILLA_COMBAT = Boolean.getBoolean("passthrough.vanillaCombat");
	private static final Map<Integer, Long> proxyLives = new HashMap<>();
	private static final Map<Integer, Integer> proxyTeams = new HashMap<>();
	private static final Map<Integer, Boolean> proxyJuveniles = new HashMap<>();
	private static final Map<Integer, Double> proxyScales = new HashMap<>();
	private static long hitSequence;
	public static final String PROXY_TAG = "gta_proxy";
	/** Mobs hunt proxies this far away (blocks). */
	private static final double FOLLOW_RANGE = 48.0;
	/** Host people further than this from the player aren't mirrored, nor mobs reported (blocks). */
	private static final double REPORT_RANGE = 96.0;

	/** The newest "peds" list from the host, flattened [handle, x, y, z, ...] (WebSocket thread -> server tick). */
	private record PedSnapshot(double[] values, int stride) {}
	private static final AtomicReference<PedSnapshot> peds = new AtomicReference<>();
	private static volatile long pedsNanos;
	/** Host damage to mobs, [entity id, amount] (WebSocket thread -> server tick). */
	private static final ConcurrentLinkedQueue<double[]> damage = new ConcurrentLinkedQueue<>();

	// server thread only
	private static final Map<Integer, Villager> proxies = new HashMap<>();
	private static final Map<Integer, Integer> handleOf = new HashMap<>();
	private static final Map<Integer, Integer> missingTicks = new HashMap<>();
	private static boolean reportedMobs;

	private MobWar() {
	}

	/** Brain-driven hostiles: they pick targets from brain memories, not goals, so they'd never join the fight. */
	private static final java.util.Set<String> BRAIN_MOBS = java.util.Set.of("piglin", "piglin_brute", "hoglin", "zoglin", "breeze", "creaking", "warden");

	/**
	 * A host person's stand-in. The server knows its tag; the client only sees that it's an invisible villager (a
	 * permanent hidden invisibility effect keeps that flag set: Minecraft resets it on sync otherwise).
	 */
	public static boolean isProxy(final Entity e) {
		return e instanceof Villager && (e.entityTags().contains(PROXY_TAG) || (e.level().isClientSide() && e.isInvisible()));
	}

	/** Mobs that join the fight: hostile, goal-driven ones. */
	public static boolean fighter(final Entity e) {
		return e instanceof Mob && !isProxy(e) && (e instanceof Enemy || BRAIN_MOBS.contains(BuiltInRegistries.ENTITY_TYPE.getKey(e.getType()).getPath()));
	}

	public static void peds(final double[] flat) {
		peds(flat, VANILLA_COMBAT ? 7 : 4);
	}

	public static void peds(final double[] flat, final int stride) {
		if (stride != (VANILLA_COMBAT ? 7 : 4) && !(VANILLA_COMBAT && (stride == 8 || stride == 10))) return;
		peds.set(new PedSnapshot(flat, stride));
		pedsNanos = System.nanoTime();
	}

	static Villager proxy(int handle) { return proxies.get(handle); }
	static int handle(Entity entity) { return handleOf.getOrDefault(entity.getId(), -1); }
	static long life(int handle) { return proxyLives.getOrDefault(handle, 0L); }
	static int team(int handle) { return proxyTeams.getOrDefault(handle, 0); }
	static boolean juvenile(int handle) { return proxyJuveniles.getOrDefault(handle, false); }
	static double scale(int handle) { return proxyScales.getOrDefault(handle, 1.0); }
	static java.util.Collection<Villager> actors() { return proxies.values(); }
	public static boolean isEnemyProxy(Entity entity) {
		return isProxy(entity) && (!VANILLA_COMBAT || team(handle(entity)) == 2);
	}

	/** Called after vanilla armour/effects/i-frames have resolved damage on the stand-in. */
	public static void onProxyDamage(final LivingEntity proxy, final DamageSource source, final float amount) {
		Integer handle = handleOf.get(proxy.getId());
		if (handle == null || !Float.isFinite(amount) || amount <= 0 || !Passthrough.active) return;
		String kind = source.getMsgId();
		var from = source.getSourcePosition();
		if (from == null) from=proxy.position();
		var hit=proxy.position().add(0,proxy.getBbHeight()*0.65,0);
		if(source.getDirectEntity() instanceof net.minecraft.world.entity.projectile.Projectile projectile) {
			var box=proxy.getBoundingBox();var at=projectile.position();
			hit=new net.minecraft.world.phys.Vec3(Math.clamp(at.x,box.minX,box.maxX),Math.clamp(at.y,box.minY,box.maxY),Math.clamp(at.z,box.minZ,box.maxZ));
		}
		Passthrough.events.accept(String.format(Locale.ROOT,
			"{\"t\":\"actorhit\",\"seq\":%d,\"h\":%d,\"life\":%d,\"d\":%.4f,\"k\":\"%s\",\"player\":%b,\"from\":[%.4f,%.4f,%.4f],\"hit\":[%.4f,%.4f,%.4f]}",
			++hitSequence, handle, proxyLives.getOrDefault(handle, 0L), amount, kind,source.getEntity() instanceof net.minecraft.server.level.ServerPlayer,from.x,from.y,from.z,hit.x,hit.y,hit.z));
	}

	public static void damage(final int id, final double amount) {
		damage.add(new double[] {id, amount});
	}

	/** New mobs (and mobs loaded again) hunt proxies; stale proxies from an earlier session are removed. */
	static void onEntityLoad(final Entity e, final ServerLevel level) {
		if (e instanceof Villager v && v.entityTags().contains(PROXY_TAG) && !proxies.containsValue(v)) {
			v.discard();
			return;
		}

		if (!fighter(e) || (VANILLA_COMBAT && e instanceof EnderDragon)) {
			return;
		}

		Mob mob = (Mob) e;
		GoalSelector targets = ((MobAccessor) mob).passthrough$targetSelector();
		if (targets.getAvailableGoals().stream().noneMatch(w -> w.getGoal() instanceof ProxyTargetGoal)) {
			targets.addGoal(0, new ProxyTargetGoal(mob));
		}

		AttributeInstance range = mob.getAttribute(Attributes.FOLLOW_RANGE);
		if (range != null && range.getBaseValue() < FOLLOW_RANGE) {
			range.setBaseValue(FOLLOW_RANGE);
		}
	}

	/** A proxy was hurt (server thread, from LivingEntity.hurtServer; the damage itself is always cancelled). */
	public static void onProxyHit(final LivingEntity proxy, final DamageSource source, final float amount) {
		Integer handle = handleOf.get(proxy.getId());
		Entity attacker = source.getEntity();
		if (source.getDirectEntity() instanceof Projectile projectile && !(attacker instanceof Player)) {
			projectile.discard(); // a skeleton's arrow ends in the person it hit
		}

		// explosions are the host's own already (every Minecraft explosion is mirrored); Steve's hits are handled there too
		if (handle == null || attacker == null || attacker instanceof Player || source.is(DamageTypeTags.IS_EXPLOSION)) {
			return;
		}

		String kind = BuiltInRegistries.ENTITY_TYPE.getKey(attacker.getType()).getPath();
		Passthrough.events.accept(String.format(Locale.ROOT, "{\"t\":\"mobhit\",\"h\":%d,\"d\":%.2f,\"from\":[%.3f,%.3f,%.3f],\"k\":\"%s\"}",
			handle, amount, attacker.getX(), attacker.getY(), attacker.getZ(), kind));
	}

	/** Every server tick (server thread). */
	static void tick(final MinecraftServer s) {
		ServerLevel level = s.overworld();
		syncProxies(level);
		if (VANILLA_COMBAT) ActorInteractions.tick(level);
		if (VANILLA_COMBAT) DanceGather.tick(level);
		if (VANILLA_COMBAT) for (Villager v : proxies.values()) {
			// Teleported host actors do not walk through Entity.move. Still run vanilla contact
			// effects (lava, fire, cactus, berry bushes, pressure plates) at their current box.
			v.noPhysics = false;
			try { v.applyEffectsFromBlocks(v.position(), v.position()); }
			finally { v.noPhysics = true; }
		}
		applyDamage(level);
        if(VANILLA_COMBAT && Passthrough.active && level.getGameTime()%10==0){
            for(Entity e:level.getAllEntities()) if(fighter(e)&&e instanceof Mob mob && !DanceGather.controls(mob) && !(mob instanceof EnderDragon) && mob.isAlive()){
                LivingEntity enemy=nearestProxy(level,mob);
                if(enemy!=null){
                    mob.setTarget(enemy);
                    if(BRAIN_MOBS.contains(BuiltInRegistries.ENTITY_TYPE.getKey(e.getType()).getPath()))
                        mob.getBrain().setMemory(net.minecraft.world.entity.ai.memory.MemoryModuleType.ATTACK_TARGET,enemy);
                }
            }
        }
		if (Passthrough.active) {
			reportMobs(s, level);
		}
	}

	private static void syncProxies(final ServerLevel level) {
		if (!proxies.isEmpty() && System.nanoTime() - pedsNanos > 1_500_000_000L) {
			clearProxies(); // the host stopped sending: nobody to hunt
			return;
		}

		PedSnapshot snapshot = peds.getAndSet(null);
		if (snapshot == null) {
			return;
		}
		double[] p = snapshot.values();

		Map<Integer, Boolean> seen = new HashMap<>();
		int stride = snapshot.stride();
		for (int i = 0; i + stride - 1 < p.length; i += stride) {
			int handle = (int) p[i];
			double x = p[i + 1], y = p[i + 2], z = p[i + 3];
			seen.put(handle, true);
			Villager v = proxies.get(handle);
			long life = VANILLA_COMBAT ? (long)p[i + 5] : 0;
			if (v != null && VANILLA_COMBAT && proxyLives.getOrDefault(handle, -1L) != life) {
				ActorInteractions.remove(handle);
				handleOf.remove(v.getId()); v.discard(); proxies.remove(handle); v = null;
			}
			proxyLives.put(handle, life);
			proxyTeams.put(handle, stride >= 8 ? (int)p[i + 7] : 2);
			proxyScales.put(handle, stride == 10 ? Math.clamp(p[i + 8], .25, 2) : 1.0);
			proxyJuveniles.put(handle, stride == 10 && p[i + 9] != 0);
			if (v == null || v.isRemoved()) {
				v = EntityTypes.VILLAGER.create(level, EntitySpawnReason.COMMAND);
				if (v == null) {
					continue;
				}

				v.setInvisible(true);
				v.addEffect(new MobEffectInstance(MobEffects.INVISIBILITY, MobEffectInstance.INFINITE_DURATION, 0, false, false), null);
				v.setNoAi(true);
				v.setNoGravity(true);
				// Host motion owns the body. Avoid artificial suffocation in coarse host barrier voxels.
				if (VANILLA_COMBAT) v.noPhysics = true;
				v.setSilent(true);
				v.addTag(PROXY_TAG);
				v.snapTo(x, y, z, 0.0F, 0.0F);
				proxies.put(handle, v); // before adding: the load event must not take it for a stale one
				if (!level.addFreshEntity(v)) {
					proxies.remove(handle);
					continue;
				}

				handleOf.put(v.getId(), handle);
			} else if (!ActorInteractions.controls(handle)) {
				v.setPos(x, y, z);
			}
			if (VANILLA_COMBAT) {
				boolean baby = juvenile(handle);
				if (v.isBaby() != baby) v.setBaby(baby);
				if (baby) v.setAge(-24000); // Host child identity does not age up between snapshots.
				var size = v.getAttribute(Attributes.SCALE);
				if (size != null) size.setBaseValue(scale(handle) / (baby ? .5 : 1));
				v.setHealth((float)Math.max(0.2, Math.min(20.0, p[i + 4] / 5.0)));
				v.setOnGround(p[i + 6] != 0);
			}

			missingTicks.remove(handle);
		}

		// hysteresis: a person missing from a few lists in a row (out of range, dead, despawned) loses their proxy
		for (Iterator<Map.Entry<Integer, Villager>> it = proxies.entrySet().iterator(); it.hasNext();) {
			Map.Entry<Integer, Villager> e = it.next();
			if (seen.containsKey(e.getKey())) {
				continue;
			}

			int missing = missingTicks.merge(e.getKey(), 1, Integer::sum);
			if (missing > 6 || e.getValue().isRemoved()) {
				ActorInteractions.remove(e.getKey());
				handleOf.remove(e.getValue().getId());
				e.getValue().discard();
				missingTicks.remove(e.getKey());
				proxyLives.remove(e.getKey());
				proxyTeams.remove(e.getKey());
				proxyScales.remove(e.getKey());
				proxyJuveniles.remove(e.getKey());
				it.remove();
			}
		}
	}

	private static void applyDamage(final ServerLevel level) {
		for (double[] d; (d = damage.poll()) != null;) {
			if (!(level.getEntity((int) d[0]) instanceof Mob mob) || !mob.isAlive()) {
				continue;
			}

			// from the nearest person (a cop, most likely), so the mob turns on them
			LivingEntity from = nearestProxy(level, mob);
			DamageSource source = from != null ? level.damageSources().mobAttack(from) : level.damageSources().generic();
			mob.damageCooldownTime = 0; // automatic fire lands several hits inside the usual 10-tick cooldown
			float before=mob.getHealth();
			// Dragons accept player or tagged explosion damage through their multipart head.
			// This damage source does not create an explosion or destroy any blocks.
			boolean applied=mob instanceof EnderDragon dragon
				? dragon.hurt(level, dragon.head, level.damageSources().explosion(null, from), (float)d[1])
				: mob.hurtServer(level, source, (float) d[1]);
			Passthrough.events.accept(String.format(java.util.Locale.ROOT,"{\"t\":\"mobdamage\",\"id\":%d,\"before\":%.3f,\"after\":%.3f,\"requested\":%.3f,\"applied\":%s}",mob.getId(),before,mob.getHealth(),d[1],applied));
		}
	}

	private static LivingEntity nearestProxy(final ServerLevel level, final Mob mob) {
		LivingEntity best = null;
		double bestD = 48.0 * 48.0;
		for (Villager v : proxies.values()) {
			if (!isEnemyProxy(v)) continue;
			double dd = v.distanceToSqr(mob);
			if (!v.isRemoved() && dd < bestD) {
				best = v;
				bestD = dd;
			}
		}

		return best;
	}

	/** {"t":"mobs","m":[[id,"zombie",x,y,z],...]}: every fighting mob near the player, every tick while there are any. */
	private static void reportMobs(final MinecraftServer s, final ServerLevel level) {
		ServerPlayer player = s.getPlayerList().getPlayers().isEmpty() ? null : s.getPlayerList().getPlayers().get(0);
		if (player == null) {
			return;
		}

		List<Entity> near = level.getEntities((Entity) null, player.getBoundingBox().inflate(REPORT_RANGE), MobWar::fighter);
		if (near.isEmpty() && !reportedMobs) {
			return;
		}

		StringBuilder b = new StringBuilder("{\"t\":\"mobs\",\"m\":[");
		int n = 0;
		for (Entity e : near) {
			if (!e.isAlive()) {
				continue;
			}
			if (VANILLA_COMBAT && e instanceof EnderDragon dragon) directDragon(level, dragon);

			var aim = e instanceof EnderDragon dragon ? dragon.head.getBoundingBox().getCenter() : e.position().add(0,.9,0);
			b.append(n++ == 0 ? "" : ",").append(String.format(Locale.ROOT, "[%d,\"%s\",%.3f,%.3f,%.3f,%.3f,%.3f,%.3f]",
				e.getId(), BuiltInRegistries.ENTITY_TYPE.getKey(e.getType()).getPath(), e.getX(), e.getY(), e.getZ(),aim.x,aim.y,aim.z));
		}

		Passthrough.events.accept(b.append("]}").toString());
		reportedMobs = n > 0;
	}

	private static void directDragon(ServerLevel level, EnderDragon dragon) {
		var phases=dragon.getPhaseManager();
		if (phases.getCurrentPhase().getPhase()==EnderDragonPhase.DYING) return;
		LivingEntity target=dragon.getTarget();
		if (target==null || !target.isAlive() || !isEnemyProxy(target) || target.distanceToSqr(dragon)>96*96) {
			target=null; double best=96*96;
			for (Villager actor:proxies.values()) if(actor.isAlive() && isEnemyProxy(actor)) {
				double distance=actor.distanceToSqr(dragon);
				if(distance<best){target=actor;best=distance;}
			}
		}
		boolean changed=dragon.getTarget()!=target;
		dragon.setTarget(target);
		if(target==null){phases.setPhase(EnderDragonPhase.HOVERING);return;}
		if(changed || phases.getCurrentPhase().getPhase()!=EnderDragonPhase.STRAFE_PLAYER){
			dragon.setFightOrigin(target.blockPosition());
			phases.setPhase(EnderDragonPhase.STRAFE_PLAYER);
			phases.getPhase(EnderDragonPhase.STRAFE_PLAYER).setTarget(target);
			Passthrough.LOG.info("dragon strafe id={} targetHost={} team={}",dragon.getId(),handle(target),team(handle(target)));
		}
	}

	/**
	 * Spawn `count` mobs of `kind` (e.g. "zombie") on the ground `minR`..`maxR` blocks from the player, within `arc`
	 * degrees either side of where the player faces (or of `yawOffset` from it); or, given `at` ({x, y, z}: a fixed
	 * spot, y near its ground), all round that spot instead, wherever the player is.
	 */
	public static void spawn(final String kind, final int count, final double minR, final double maxR, final double arc, final double yawOffset,
		final double[] at) {
		MinecraftServer s = serverOrNull();
		if (s == null) {
			return;
		}

		s.execute(() -> {
			ServerLevel level = s.overworld();
			ServerPlayer player = s.getPlayerList().getPlayers().isEmpty() ? null : s.getPlayerList().getPlayers().get(0);
			Optional<EntityType<?>> type = BuiltInRegistries.ENTITY_TYPE.getOptional(Identifier.withDefaultNamespace(kind));
			if (player == null || type.isEmpty()) {
				Passthrough.LOG.warn("spawnmobs: no player or unknown mob {}", kind);
				return;
			}

			ThreadLocalRandom random = ThreadLocalRandom.current();
			double cx = at != null ? at[0] : player.getX(), cy = at != null ? at[1] : player.getY(), cz = at != null ? at[2] : player.getZ();
			double baseYaw = at != null ? 0.0 : player.getYRot() + yawOffset, spread = at != null ? 180.0 : arc;
			int spawned = 0;
			for (int i = 0; i < count * 4 && spawned < count; i++) {
				double yaw = Math.toRadians(baseYaw + (random.nextDouble() * 2.0 - 1.0) * spread);
				double r = minR + random.nextDouble() * (maxR - minR);
				int x = (int) Math.floor(cx - Math.sin(yaw) * r), z = (int) Math.floor(cz + Math.cos(yaw) * r);
				BlockPos ground = groundAt(level, x, (int) Math.floor(cy), z);
				if (ground == null) {
					continue; // no host ground there (yet)
				}

				Entity e = type.get().spawn(level, ground, EntitySpawnReason.COMMAND);
				if (e instanceof Mob mob) {
					mob.setPersistenceRequired();
					spawned++;
				}
			}

			Passthrough.LOG.info("spawnmobs: {} x {}", spawned, kind);
		});
	}

	/** The free block above the host's ground near height y in column (x, z), or null. */
	private static BlockPos groundAt(final ServerLevel level, final int x, final int y, final int z) {
		BlockPos.MutableBlockPos p = new BlockPos.MutableBlockPos();
		for (int dy = 6; dy >= -12; dy--) {
			p.set(x, y + dy, z);
			if (!level.getBlockState(p).isAir() && level.getBlockState(p.above()).isAir() && level.getBlockState(p.above(2)).isAir()) {
				return p.above().immutable();
			}
		}

		return null;
	}

	/** Remove every fighting mob (not the player's other things). */
	public static void clearMobs() {
		MinecraftServer s = serverOrNull();
		if (s != null) {
			s.execute(() -> discardFighters(s.overworld()));
		}
	}

	/** Explicit director cleanup: only loaded real mobs, never builds, display entities or host actors. */
	public static void clearAllMobs(String requestId) {
		MinecraftServer server=serverOrNull();
		String id=requestId==null?"":requestId.substring(0,Math.min(128,requestId.length()));
		if(server==null){
			JsonObject result=new JsonObject();result.addProperty("t","clearallmobs_result");
			result.addProperty("requestId",id);result.addProperty("error","world_not_loaded");
			Passthrough.events.accept(result.toString());return;
		}
		server.execute(()->{
			int removed=0,players=0,paintings=0,itemFrames=0,nonMob=0,hostProxies=0;
			JsonObject byType=new JsonObject(),byDimension=new JsonObject();
			for(ServerLevel level:server.getAllLevels()){
				int dimensionRemoved=0;
				List<Entity> snapshot=new ArrayList<>();level.getAllEntities().forEach(snapshot::add);
				for(Entity entity:snapshot){
					if(entity.isRemoved())continue;
					if(isProxy(entity)){hostProxies++;continue;}
					if(entity instanceof Mob){
						String type=BuiltInRegistries.ENTITY_TYPE.getKey(entity.getType()).toString();
						entity.discard();removed++;dimensionRemoved++;
						byType.addProperty(type,byType.has(type)?byType.get(type).getAsInt()+1:1);
					}else{
						nonMob++;
						if(entity instanceof Player)players++;
						else if(entity instanceof net.minecraft.world.entity.decoration.painting.Painting)paintings++;
						else if(entity instanceof net.minecraft.world.entity.decoration.ItemFrame)itemFrames++;
					}
				}
				byDimension.addProperty(level.dimension().identifier().toString(),dimensionRemoved);
			}
			JsonObject kept=new JsonObject();kept.addProperty("players",players);kept.addProperty("paintings",paintings);
			kept.addProperty("itemFrames",itemFrames);kept.addProperty("nonMob",nonMob);kept.addProperty("hostProxies",hostProxies);
			JsonObject result=new JsonObject();result.addProperty("t","clearallmobs_result");result.addProperty("requestId",id);
			result.addProperty("scope","loaded_entities_all_dimensions");result.addProperty("removed",removed);
			result.add("byType",byType);result.add("byDimension",byDimension);result.add("preserved",kept);
			Passthrough.LOG.info("loaded mob cleanup {}",result);Passthrough.events.accept(result.toString());
		});
	}

	/** Server thread. */
	static void discardFighters(final ServerLevel level) {
		List<Entity> all = new ArrayList<>();
		level.getAllEntities().forEach(all::add);
		all.stream().filter(e -> fighter(e) || isProxy(e)).forEach(Entity::discard);
	}

	/** The host went away: nobody left to hunt. */
	public static void hostGone() {
		MinecraftServer s = serverOrNull();
		if (s != null) {
			s.execute(MobWar::clearProxies);
		}
	}

	private static void clearProxies() {
		ActorInteractions.clear();
		proxies.values().forEach(Entity::discard);
		proxies.clear();
		handleOf.clear();
		missingTicks.clear();
		proxyLives.clear();
		proxyTeams.clear();
		proxyScales.clear();
		proxyJuveniles.clear();
	}

	static void detach(final MinecraftServer s) {
		// the world is saved next: none of the fight is kept in it
		ActorInteractions.clear();
		discardFighters(s.overworld());
		proxies.clear();
		handleOf.clear();
		missingTicks.clear();
		proxyLives.clear();
		proxyTeams.clear();
		proxyScales.clear();
		proxyJuveniles.clear();
		peds.set(null);
		damage.clear();
		reportedMobs = false;
	}

	private static MinecraftServer serverOrNull() {
		return WorldBridge.server();
	}

	public static void inspectCombat() {
		MinecraftServer s=serverOrNull(); if(s==null)return;
		s.execute(()->{for(Entity e:s.overworld().getAllEntities())if(fighter(e)&&e instanceof Mob mob)Passthrough.LOG.info("combat mob id={} kind={} targetHost={} hp={}",mob.getId(),BuiltInRegistries.ENTITY_TYPE.getKey(mob.getType()),mob.getTarget()==null?null:handleOf.get(mob.getTarget().getId()),mob.getHealth());for(ServerPlayer p:s.getPlayerList().getPlayers()) Passthrough.LOG.info("combat server player using={} ticks={} held={} pos={} rot={},{}",p.isUsingItem(),p.getTicksUsingItem(),p.getMainHandItem(),p.position(),p.getYRot(),p.getXRot());});
	}
}
