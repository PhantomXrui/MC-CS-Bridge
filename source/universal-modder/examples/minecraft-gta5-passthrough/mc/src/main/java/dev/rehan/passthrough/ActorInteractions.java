package dev.rehan.passthrough;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Set;
import net.minecraft.core.registries.BuiltInRegistries;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.core.particles.ParticleTypes;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.npc.villager.Villager;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.entity.vehicle.boat.AbstractBoat;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.Vec3;
import net.minecraft.world.phys.shapes.CollisionContext;
import net.minecraft.world.phys.shapes.Shapes;
import net.minecraft.world.phys.shapes.VoxelShape;

/** Local CS bots use actual MC leads and boats. Every reverse pose remains host-validated. */
public final class ActorInteractions {
    private ActorInteractions() {}
    private static final int CT = 3;
    private static final int T = 2;
    private static final Path CONFIG = Path.of(System.getProperty("passthrough.actorInteractionsConfig", Path.of(System.getenv().getOrDefault("MCCS_ROOT", "."), "config", "actor-interactions.json").toString()));
    private static volatile Set<String> feedItems = Set.of("minecraft:golden_apple");
    private static long nextConfigCheck, configStamp = -1;
    private static final Map<Integer, State> states = new HashMap<>();
    private static final Map<Long, Long> birthCooldowns = new HashMap<>();
    private static long sequence;
    private static String lastAction = "none";
    private static final class State {
        final long life;
        boolean controlled;
        int mate = -1, courtTicks, nearTicks;
        long mateLife;
        long loveUntil, cooldownUntil, pending, pendingUntil;
        boolean enchanted;
        long lastInteractionTick = Long.MIN_VALUE;
        State(long life) { this.life = life; }
    }
    private static State state(int h) {
        return states.computeIfAbsent(h, k -> {
            State s = new State(MobWar.life(h));
            s.cooldownUntil = birthCooldowns.getOrDefault(s.life, 0L);
            return s;
        });
    }
    private static boolean eligible(Villager actor) {
        return Passthrough.active && MobWar.VANILLA_COMBAT && actor != null && actor.isAlive()
            && !actor.isRemoved() && MobWar.handle(actor) >= 0;
    }
    private static boolean canBreed(Villager actor) {
        return eligible(actor) && MobWar.team(MobWar.handle(actor)) == T && !MobWar.juvenile(MobWar.handle(actor));
    }
    private static boolean feedItem(net.minecraft.world.item.ItemStack stack) {
        return !stack.is(Items.ENCHANTED_GOLDEN_APPLE)
            && feedItems.contains(BuiltInRegistries.ITEM.getKey(stack.getItem()).toString());
    }
    private static void reloadConfig() {
        long now = System.currentTimeMillis();
        if (now < nextConfigCheck) return;
        nextConfigCheck = now + 5000;
        try {
            if (!Files.exists(CONFIG)) return;
            long stamp = Files.getLastModifiedTime(CONFIG).toMillis();
            if (stamp == configStamp) return;
            var root = JsonParser.parseString(Files.readString(CONFIG)).getAsJsonObject();
            var configured = new java.util.HashSet<String>();
            for (var item : root.getAsJsonArray("feedItems")) {
                var id = net.minecraft.resources.Identifier.parse(item.getAsString());
                if (id.toString().equals("minecraft:enchanted_golden_apple")) continue;
                if (BuiltInRegistries.ITEM.getOptional(id).isPresent()) configured.add(id.toString());
            }
            if (configured.isEmpty() || configured.size() > 16) throw new IllegalArgumentException("feedItems needs 1..16 installed item IDs");
            feedItems = Set.copyOf(configured); configStamp = stamp;
            Passthrough.LOG.info("Actor breeding food loaded: {}", feedItems);
        } catch (Exception e) { Passthrough.LOG.warn("Actor food config invalid; retaining {}: {}", feedItems, e.toString()); }
    }
    static boolean controls(int h) {
        State s = states.get(h);
        return s != null && s.controlled && s.life == MobWar.life(h);
    }

    /** The client reserves these entity clicks even when the server will reject range/team/cooldown. */
    public static boolean hasInteractionItem(Villager actor, Player player, InteractionHand hand) {
        var held = player.getItemInHand(hand);
        boolean releaseItem = held.is(Items.SHEARS) || (held.isEmpty() && (actor.isLeashed() || actor.isPassenger()));
        int handle = MobWar.handle(actor);
        boolean danceStick = held.is(Items.STICK) && handle >= 0 && MobWar.team(handle) == T;
        boolean ritualRod = held.is(Items.END_ROD) && handle >= 0 && MobWar.team(handle) == T;
        boolean enchantedFood = held.is(Items.ENCHANTED_GOLDEN_APPLE);
        return held.is(Items.LEAD) || feedItem(held) || releaseItem || danceStick || ritualRod || enchantedFood;
    }

    /** Used by physical right-click and the authenticated diagnostic entry point. */
    public static InteractionResult interact(Villager actor, Player player, InteractionHand hand) {
        if (!hasInteractionItem(actor, player, hand)) return InteractionResult.PASS;
        var held = player.getItemInHand(hand);
        boolean releaseItem = held.is(Items.SHEARS) || (held.isEmpty() && (actor.isLeashed() || actor.isPassenger()));
        boolean food = feedItem(held) || held.is(Items.ENCHANTED_GOLDEN_APPLE);
        // The client does not own host team metadata; the integrated server makes the decision.
        if (actor.level().isClientSide()) return InteractionResult.SUCCESS;
        if (!eligible(actor) || !(player instanceof ServerPlayer sp)
            || !player.isWithinEntityInteractionRange(actor, 0)) {
            note("interaction-denied", actor, "living host bot in reach required");
            return InteractionResult.CONSUME;
        }
        State interaction = state(MobWar.handle(actor));
        long interactionTick = actor.level().getGameTime();
        // Match vanilla's four-tick use cadence; duplicate hand/packet events cannot consume twice.
        if (interaction.lastInteractionTick != Long.MIN_VALUE && interactionTick - interaction.lastInteractionTick < 4)
            return InteractionResult.CONSUME;
        interaction.lastInteractionTick = interactionTick;
        if (held.is(Items.STICK)) {
            if (MobWar.team(MobWar.handle(actor)) != T) {
                note("dance-denied", actor, "T agent required");
                return InteractionResult.CONSUME;
            }
            // CS 插件持有权威舞蹈状态，所以重复右键就是关闭。
            Passthrough.events.accept("{\"t\":\"director\",\"host\":\"cs2\",\"op\":\"dance_toggle\"}");
            note("dance-toggle", actor, "stick");
            return InteractionResult.SUCCESS_SERVER;
        }
        if (held.is(Items.END_ROD)) {
            if (MobWar.team(MobWar.handle(actor)) != T) {
                note("ritual-denied", actor, "T agent required");
                return InteractionResult.CONSUME;
            }
            if (ritualHandle < 0) startRitual(actor, sp);
            return InteractionResult.SUCCESS_SERVER;
        }
        if (food) {
            if (canBreed(actor)) feed(actor, sp, hand);
            else note("feed-denied", actor, "adult T agent required");
            return InteractionResult.SUCCESS_SERVER;
        }
        if (!canLead(actor)) {
            note("interaction-denied", actor, "living T or CT agent required");
            return InteractionResult.CONSUME;
        }
        if (releaseItem || (player.isShiftKeyDown() && held.is(Items.LEAD))) {
            release(MobWar.handle(actor), "player-release");
        } else if (actor.isPassenger()) {
            // Repeated held-button packets must not undo boarding immediately.
            return InteractionResult.SUCCESS_SERVER;
        } else if (actor.isLeashed()) {
            board(actor);
        } else {
            actor.setLeashedTo(player, true);
            held.consume(1, player);
            state(MobWar.handle(actor)).controlled = true;
            note("leashed", actor, "player");
        }
        return InteractionResult.SUCCESS_SERVER;
    }

    /** 名牌“Bilibili:Phantom_rui”右键：准星处放固定 ID；潜行+右键：切换跟随视角的 ID。 */
    static boolean isWatermarkItem(ItemStack stack) {
        return stack.is(Items.NAME_TAG) && stack.getHoverName().getString().contains("Bilibili:Phantom_rui");
    }

    public static InteractionResult useWatermark(Player player, Level world, InteractionHand hand) {
        ItemStack held = player.getItemInHand(hand);
        if (!isWatermarkItem(held)) return InteractionResult.PASS;
        if (world.isClientSide()) return InteractionResult.SUCCESS;
        boolean follow = player.isShiftKeyDown();
        Passthrough.events.accept("{\"t\":\"director\",\"host\":\"cs2\",\"op\":\"" + (follow ? "watermark_follow" : "watermark") + "\"}");
        return InteractionResult.SUCCESS;
    }

    // ---- 末地烛召唤仪式：玩家在前、T 在后；爱心 + 小幅前后来回；结束后生成一只小 T ----
    private static int ritualHandle = -1;
    private static int ritualPlayerId = -1;
    private static long ritualStart;
    private static final int RITUAL_TICKS = 60;

    // 附魔金苹果多胎：繁殖成功时按 1.0/1.5/2.0/2.5 秒连续生成 4 只小 T。
    private static final java.util.List<double[]> multiBirths = new java.util.ArrayList<>();

    private static void scheduleMultiBirth(long now, double x, double y, double z) {
        for (long delay : new long[]{20, 30, 40, 50}) multiBirths.add(new double[]{now + delay, x, y, z});
    }

    private static void startRitual(Villager actor, ServerPlayer player) {
        ritualHandle = MobWar.handle(actor);
        ritualPlayerId = player.getId();
        ritualStart = actor.level().getGameTime();
        Passthrough.events.accept("{\"t\":\"director\",\"host\":\"cs2\",\"op\":\"camera\",\"mode\":1}");
        Voice.play(Voice.DEPLOY);
        note("ritual-start", actor, "end_rod");
    }

    private static void endRitual(boolean born, ServerPlayer player) {
        ritualHandle = -1;
        ritualPlayerId = -1;
        Passthrough.events.accept("{\"t\":\"director\",\"host\":\"cs2\",\"op\":\"camera\",\"mode\":0}");
        if (born && player != null) {
            Passthrough.events.accept(String.format(Locale.ROOT,
                "{\"t\":\"spawnjuvenile\",\"seq\":%d,\"x\":%.3f,\"y\":%.3f,\"z\":%.3f}",
                System.nanoTime(), player.getX(), player.getY(), player.getZ()));
        }
    }

    private static void tickRitual(ServerLevel level, long now) {
        if (ritualHandle < 0) return;
        Villager actor = MobWar.proxy(ritualHandle);
        ServerPlayer player = null;
        for (ServerPlayer p : level.getServer().getPlayerList().getPlayers()) {
            if (p.getId() == ritualPlayerId) { player = p; break; }
        }
        long elapsed = now - ritualStart;
        if (actor == null || player == null || !player.isAlive() || !eligible(actor) || elapsed > RITUAL_TICKS + 20) {
            endRitual(false, null);
            return;
        }
        Vec3 look = player.getLookAngle();
        double bob = Math.sin(elapsed * 0.35) * 0.3;
        Vec3 at = player.position().subtract(look.scale(0.6)).add(look.scale(bob));
        actor.setPos(at.x, player.getY(), at.z);
        actor.setYRot((float) Math.toDegrees(Math.atan2(-(player.getX() - at.x), player.getZ() - at.z)));
        if (elapsed % 5 == 0) hearts(level, actor);
        if (elapsed >= RITUAL_TICKS) endRitual(true, player);
    }

    private static boolean canLead(Villager actor) {
        int team = MobWar.team(MobWar.handle(actor));
        return eligible(actor) && (team == T || team == CT);
    }

    private static boolean board(Villager actor) {
        if (!canLead(actor) || !actor.isLeashed() || actor.isPassenger()) return false;
        var boats = new java.util.ArrayList<AbstractBoat>();
        for (Entity entity : actor.level().getEntities(actor, actor.getBoundingBox().inflate(2.25),
                e -> e instanceof AbstractBoat && e.isAlive())) {
            AbstractBoat boat = (AbstractBoat)entity;
            if (actor.distanceToSqr(boat) < 2.25 * 2.25) boats.add(boat);
        }
        // startRiding performs the boat's actual capacity checks and vanilla lead drop.
        boats.sort(java.util.Comparator.comparingDouble(actor::distanceToSqr));
        for (AbstractBoat boat : boats) {
            if (!actor.startRiding(boat)) continue;
            state(MobWar.handle(actor)).controlled = true;
            actor.setDeltaMovement(Vec3.ZERO);
            note("boarded", actor, "boat=" + boat.getId());
            return true;
        }
        return false;
    }

    private static void feed(Villager actor, ServerPlayer player, InteractionHand hand) {
        int h = MobWar.handle(actor);
        State s = state(h);
        long now = actor.level().getGameTime();
        if (now < s.cooldownUntil || s.pending != 0 || now < s.loveUntil) {
            note("feed-denied", actor, s.pending != 0 ? "pending" : now < s.cooldownUntil ? "cooldown" : "already-fed");
            return;
        }
        var stack = player.getItemInHand(hand);
        boolean enchanted = stack.is(Items.ENCHANTED_GOLDEN_APPLE);
        player.getItemInHand(hand).consume(1, player);
        s.loveUntil = now + 600;
        if (enchanted) s.enchanted = true;
        hearts((ServerLevel)actor.level(), actor);
        note("fed", actor, (enchanted ? "enchanted_golden_apple" : "golden_apple") + "; love=30s");
        findMate(actor, now);
    }

    private static void findMate(Villager actor, long now) {
        int h = MobWar.handle(actor);
        State a = state(h);
        if (!canBreed(actor) || a.mate >= 0 || a.pending != 0 || a.loveUntil <= now || a.cooldownUntil > now) return;
        Villager nearest = null;
        double distance = 64;
        for (Villager other : MobWar.actors()) {
            if (other == actor || !canBreed(other)) continue;
            State b = states.get(MobWar.handle(other));
            double d = actor.distanceToSqr(other);
            if (b != null && b.mate < 0 && b.pending == 0 && b.loveUntil > now && b.cooldownUntil <= now && d <= distance) {
                nearest = other; distance = d;
            }
        }
        if (nearest == null) return;
        int mate = MobWar.handle(nearest);
        State b = state(mate);
        a.mate = mate; a.mateLife = b.life; b.mate = h; b.mateLife = a.life;
        a.courtTicks = b.courtTicks = 0;
        a.nearTicks = b.nearTicks = 0;
        a.controlled = b.controlled = true;
        // Courtship owns their movement; ropes and boat seats must not pull them apart.
        if (actor.isLeashed()) actor.dropLeash();
        if (nearest.isLeashed()) nearest.dropLeash();
        actor.stopRiding(); nearest.stopRiding();
        note("courting", actor, "mate=" + mate);
    }

    private static void requestBirth(Villager actor, Villager other, long now) {
        int h = MobWar.handle(actor), mate = MobWar.handle(other);
        State a = state(h), b = state(mate);
        if (a.enchanted || b.enchanted) {
            a.enchanted = false;
            b.enchanted = false;
            a.loveUntil = b.loveUntil = 0;
            a.mate = b.mate = -1;
            a.pending = b.pending = 0;
            a.cooldownUntil = b.cooldownUntil = now + 1200;
            scheduleMultiBirth(now, (actor.getX() + other.getX()) / 2.0, Math.max(actor.getY(), other.getY()), (actor.getZ() + other.getZ()) / 2.0);
            Voice.play(Voice.DEPLOY);
            note("multi-birth", actor, "enchanted; 4 juveniles");
            return;
        }
        long request = ++sequence;
        a.pending = b.pending = request;
        a.pendingUntil = b.pendingUntil = now + 100;
        a.loveUntil = b.loveUntil = 0;
        Passthrough.events.accept(String.format(Locale.ROOT,
            "{\"t\":\"actorbreed\",\"h\":%d,\"life\":%d,\"mate\":%d,\"mateLife\":%d,\"seq\":%d}",
            h, a.life, mate, b.life, request));
        note("breed-requested", actor, "mate=" + mate + "; seq=" + request);
    }

    static void tick(ServerLevel level) {
        if (!Passthrough.active) { clear(); return; }
        reloadConfig();
        long now = level.getGameTime();
        tickRitual(level, now);
        boolean wasMulti = !multiBirths.isEmpty();
        for (var it = multiBirths.iterator(); it.hasNext(); ) {
            double[] m = it.next();
            if (now < (long) m[0]) continue;
            it.remove();
            Passthrough.events.accept(String.format(Locale.ROOT,
                "{\"t\":\"spawnjuvenile\",\"seq\":%d,\"x\":%.3f,\"y\":%.3f,\"z\":%.3f}",
                System.nanoTime(), m[1], m[2], m[3]));
        }
        if (wasMulti && multiBirths.isEmpty()) Voice.play(Voice.ACE);
        birthCooldowns.entrySet().removeIf(e -> e.getValue() <= now);
        for (Villager actor : MobWar.actors()) {
            int h = MobWar.handle(actor);
            State s = states.get(h);
            if (!eligible(actor)) {
                if (s != null && s.controlled) release(h, "ineligible");
                continue;
            }
            if (s != null && s.pending != 0 && now >= s.pendingUntil) {
                s.pending = 0;
                note("breed-timeout", actor, "no host acknowledgement");
                release(h, "breed-timeout");
            }
            if (s != null && s.loveUntil > now) {
                if (now % 20 == 0) hearts(level, actor);
                findMate(actor, now);
            }
            if (s != null && s.mate >= 0) {
                if (h < s.mate) courtship(actor, now);
                continue;
            }
            if (!canLead(actor)) continue;
            Entity holder = actor.getLeashHolder();
            boolean riding = actor.getVehicle() instanceof AbstractBoat;
            if (!riding && holder != null) {
                if (!holder.isAlive() || actor.distanceToSqr(holder) > 24 * 24) {
                    release(h, "leash-range");
                    continue;
                }
                s = state(h);
                s.controlled = true;
                // NoAI proxies do not pathfind. Pull at <=7 blocks/s, keeping the vanilla rope.
                // Host independently checks speed and static wall crossing before teleporting its bot.
                Vec3 delta = holder.position().subtract(actor.position());
                double length = delta.length();
                actor.setDeltaMovement(Vec3.ZERO);
                if (length > 2.25) {
                    Vec3 movement = delta.scale(Math.min(.35, length - 2.25) / length);
                    actor.setPos(actor.position().add(collidePlacedBlocks(actor, movement)));
                    actor.setYRot((float)Math.toDegrees(Math.atan2(-delta.x, delta.z)));
                }
                if (now % 5 == 0) board(actor);
                riding = actor.getVehicle() instanceof AbstractBoat;
            }
            if (riding) {
                s = state(h);
                s.controlled = true;
                Entity vehicle = actor.getVehicle();
                actor.setPos(vehicle.getPassengerRidingPosition(actor));
                actor.setYRot(vehicle.getYRot());
                actor.setDeltaMovement(Vec3.ZERO);
            }
            if (s != null && s.controlled) {
                if (!riding && !actor.isLeashed()) release(h, "leash-or-boat-ended");
                else sendPose(h, actor, riding ? "boat" : "leash");
            }
        }
    }

    private static void courtship(Villager actor, long now) {
        int h = MobWar.handle(actor); State a = state(h);
        Villager mate = MobWar.proxy(a.mate); State b = states.get(a.mate);
        if (!canBreed(actor) || !canBreed(mate) || b == null || b.life != a.mateLife || b.mate != h
                || actor.distanceToSqr(mate) > 12 * 12 || (a.pending == 0 && (a.loveUntil <= now || b.loveUntil <= now))) {
            release(h, "courtship-ended"); return;
        }
        a.courtTicks++; b.courtTicks = a.courtTicks;
        if (a.pending == 0) {
            approach(actor, mate); approach(mate, actor);
        }
        if (now % 10 == 0) { hearts((ServerLevel)actor.level(), actor); hearts((ServerLevel)mate.level(), mate); }
        sendPose(h, actor, "courtship"); sendPose(a.mate, mate, "courtship");
        a.nearTicks = actor.distanceToSqr(mate) <= .85 * .85 ? a.nearTicks + 1 : 0;
        b.nearTicks = a.nearTicks;
        // Stay together with visible hearts for two seconds before creating the child.
        if (a.pending == 0 && a.courtTicks >= 60 && a.nearTicks >= 40) requestBirth(actor, mate, now);
    }

    private static void approach(Villager actor, Villager mate) {
        Vec3 delta = mate.position().subtract(actor.position());
        double length = delta.length(); actor.setDeltaMovement(Vec3.ZERO);
        if (length > .75) {
            Vec3 move = delta.scale(Math.min(.06, (length - .75) / 2) / length);
            actor.setPos(actor.position().add(collidePlacedBlocks(actor, move)));
        }
        actor.setYRot((float)Math.toDegrees(Math.atan2(-delta.x, delta.z)));
    }

    /** Keep no-physics host proxies outside actual placed MC collision shapes while dragging. */
    private static Vec3 collidePlacedBlocks(Villager actor, Vec3 movement) {
        var box = actor.getBoundingBox();
        var shapes = new java.util.ArrayList<VoxelShape>();
        var level = actor.level();
        var context = CollisionContext.of(actor);
        for (BlockPos pos : BlockPos.betweenClosed(box.expandTowards(movement).inflate(.001))) {
            var block = level.getBlockState(pos);
            if (WorldBridge.isHostSupport(block, pos)) continue;
            var shape = block.getCollisionShape(level, pos, context);
            if (!shape.isEmpty()) shapes.add(shape.move(pos));
        }
        double y = Shapes.collide(Direction.Axis.Y, box, shapes, movement.y);
        box = box.move(0, y, 0);
        double x = Shapes.collide(Direction.Axis.X, box, shapes, movement.x);
        box = box.move(x, 0, 0);
        double z = Shapes.collide(Direction.Axis.Z, box, shapes, movement.z);
        return new Vec3(x, y, z);
    }

    private static void hearts(ServerLevel level, Villager actor) {
        level.sendParticles(ParticleTypes.HEART, actor.getX(), actor.getY() + 1.7, actor.getZ(), 5, .3, .2, .3, 0);
    }
    static void sendPose(int h, Villager actor, String mode) {
        Passthrough.events.accept(String.format(Locale.ROOT,
            "{\"t\":\"actorcontrol\",\"h\":%d,\"life\":%d,\"seq\":%d,\"p\":[%.5f,%.5f,%.5f],\"yaw\":%.3f,\"mode\":\"%s\"}",
            h, MobWar.life(h), ++sequence, actor.getX(), actor.getY(), actor.getZ(), actor.getYRot(), mode));
    }
    private static void release(int h, String reason) {
        Villager actor = MobWar.proxy(h);
        State s = states.get(h);
        if (s != null && s.mate >= 0) {
            int mate = s.mate; s.mate = -1; s.loveUntil = 0;
            State other = states.get(mate);
            if (other != null && other.life == s.mateLife && other.mate == h && other.mateLife == s.life) {
                other.mate = -1; other.loveUntil = 0;
                release(mate, reason);
            }
        }
        if (actor != null) {
            if (actor.isLeashed()) actor.dropLeash();
            if (actor.isPassenger()) actor.stopRiding();
            actor.setDeltaMovement(Vec3.ZERO);
            if (s != null && s.controlled) sendPose(h, actor, "release");
            note("released", actor, reason);
        }
        if (s != null) {
            s.controlled = false;
            s.loveUntil = s.pending = s.pendingUntil = 0;
            s.courtTicks = 0;
        }
    }
    static void remove(int h) {
        release(h, "actor-life-ended");
        states.remove(h);
    }
    static void clear() {
        for (int h : new java.util.ArrayList<>(states.keySet())) release(h, "host-or-world-ended");
        states.clear();
        birthCooldowns.clear();
    }
    public static void receiveControlAck(JsonObject message) {
        var server = WorldBridge.server();
        if (server == null) return;
        server.execute(() -> {
            int h = message.get("h").getAsInt();
            if (message.get("life").getAsLong() == MobWar.life(h) && !message.get("ok").getAsBoolean())
                release(h, message.has("reason") ? message.get("reason").getAsString() : "host-rejected");
        });
    }
    public static void receiveBreedAck(JsonObject message) {
        var server = WorldBridge.server();
        if (server == null) return;
        server.execute(() -> {
            long request = message.get("seq").getAsLong();
            if (request <= 0) return;
            boolean ok = message.get("ok").getAsBoolean();
            long now = server.overworld().getGameTime();
            if (ok && message.has("life")) {
                long childLife = message.get("life").getAsLong();
                birthCooldowns.putIfAbsent(childLife, now + 1200);
                for (State s : states.values()) if (s.life == childLife)
                    s.cooldownUntil = Math.max(s.cooldownUntil, birthCooldowns.get(childLife));
            }
            // Set both parents' cooldowns before release clears the reciprocal courtship/pending state.
            var parents = new java.util.ArrayList<Integer>();
            for (var entry : states.entrySet()) {
                State s = entry.getValue();
                if (s.pending != request) continue;
                s.pending = 0;
                if (ok) s.cooldownUntil = now + 1200;
                parents.add(entry.getKey());
            }
            for (int h : parents) {
                Villager actor = MobWar.proxy(h);
                if (actor != null && ok) hearts(server.overworld(), actor);
                note(ok ? "bred" : "breed-rejected", actor,
                    message.has("reason") ? message.get("reason").getAsString() : "seq=" + request);
                release(h, ok ? "birth-complete" : "birth-rejected");
            }
            JsonObject event = message.deepCopy();
            event.addProperty("t", "actorbreedresult");
            Passthrough.events.accept(event.toString());
        });
    }

    /** Authenticated tests use the same item/range/team gates and actions as physical right-click. */
    public static void director(JsonObject message) {
        var server = WorldBridge.server();
        if (server == null) return;
        server.execute(() -> {
            String op = message.has("op") ? message.get("op").getAsString() : "inspect";
            Villager actor = message.has("h") ? MobWar.proxy(message.get("h").getAsInt()) : null;
            ServerPlayer player = server.getPlayerList().getPlayers().stream().findFirst().orElse(null);
            if (!op.equals("inspect") && actor != null && player != null && eligible(actor)
                    && player.isWithinEntityInteractionRange(actor, 0)) {
                switch (op) {
                    case "lead", "feed", "interact" -> {
                        if ((op.equals("lead") && !player.getMainHandItem().is(Items.LEAD))
                                || (op.equals("feed") && !feedItem(player.getMainHandItem())))
                            note("test-denied", actor, "required held item missing");
                        else interact(actor, player, InteractionHand.MAIN_HAND);
                    }
                    case "release" -> release(MobWar.handle(actor), "test-release");
                    case "board" -> { if (!board(actor)) note("board-denied", actor, "leashed bot and nearby boat required"); }
                    default -> note("test-denied", actor, "unknown operation");
                }
            } else if (!op.equals("inspect")) note("test-denied", actor, "CT bot in reach required");
            inspect(server.overworld());
        });
    }
    private static void inspect(ServerLevel level) {
        JsonObject result = new JsonObject();
        result.addProperty("t", "actorinteractionstate");
        result.addProperty("lastAction", lastAction);
        JsonArray food = new JsonArray(); feedItems.forEach(food::add); result.add("feedItems", food);
        JsonArray actors = new JsonArray();
        for (Villager actor : MobWar.actors()) {
            int h = MobWar.handle(actor);
            State s = states.get(h);
            JsonObject a = new JsonObject();
            a.addProperty("h", h); a.addProperty("life", MobWar.life(h)); a.addProperty("team", MobWar.team(h));
            a.addProperty("proxy", actor.getId()); a.addProperty("controlled", controls(h));
            a.addProperty("leashed", actor.isLeashed());
            a.addProperty("holder", actor.getLeashHolder() == null ? -1 : actor.getLeashHolder().getId());
            a.addProperty("boat", actor.getVehicle() instanceof AbstractBoat ? actor.getVehicle().getId() : -1);
            a.addProperty("loveTicks", s == null ? 0 : Math.max(0, s.loveUntil - level.getGameTime()));
            a.addProperty("cooldownTicks", s == null ? 0 : Math.max(0, s.cooldownUntil - level.getGameTime()));
            a.addProperty("pending", s == null ? 0 : s.pending);
            a.addProperty("mate", s == null ? -1 : s.mate); a.addProperty("courtTicks", s == null ? 0 : s.courtTicks);
            a.addProperty("juvenile", MobWar.juvenile(h)); a.addProperty("scale", MobWar.scale(h));
            a.addProperty("width", actor.getBbWidth()); a.addProperty("height", actor.getBbHeight());
            JsonArray p = new JsonArray(); p.add(actor.getX()); p.add(actor.getY()); p.add(actor.getZ()); a.add("p", p);
            actors.add(a);
        }
        result.add("actors", actors);
        var players = level.getServer().getPlayerList().getPlayers();
        if (!players.isEmpty()) {
            ServerPlayer player = players.getFirst();
            JsonObject p = new JsonObject();
            p.addProperty("held", player.getMainHandItem().toString());
            p.addProperty("range", player.entityInteractionRange());
            JsonArray at = new JsonArray(); at.add(player.getX()); at.add(player.getY()); at.add(player.getZ()); p.add("p", at);
            result.add("player", p);
        }
        Passthrough.events.accept(result.toString());
    }
    private static void note(String action, Villager actor, String detail) {
        lastAction = action + ": " + detail;
        JsonObject event = new JsonObject();
        event.addProperty("t", "actorinteraction"); event.addProperty("action", action);
        event.addProperty("h", actor == null ? -1 : MobWar.handle(actor)); event.addProperty("detail", detail);
        Passthrough.events.accept(event.toString());
        Passthrough.LOG.info("actor interaction {}", event);
    }
}
