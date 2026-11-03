import com.sun.tools.attach.VirtualMachine;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.time.Instant;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/** Read-only live probe. Calls no setters and performs no class transformation. */
public final class FluidLiveProbe {
    private static final Path LOG = Path.of("D:\\MC-CS-Bridge\\logs\\fluid-live-probe.txt");

    public static void main(String[] args) throws Exception {
        if (args.length != 2) throw new IllegalArgumentException("Usage: FluidLiveProbe <pid> <agent-jar>");
        VirtualMachine vm = VirtualMachine.attach(args[0]);
        try { vm.loadAgent(Path.of(args[1]).toAbsolutePath().toString(), ""); }
        finally { vm.detach(); }
        System.out.println("Read-only probe requested; output: " + LOG);
    }

    public static void agentmain(String ignored, Instrumentation instrumentation) {
        CountDownLatch done = new CountDownLatch(1);
        line("BEGIN read-only probe");
        try {
            Class<?> minecraftClass = null;
            for (Class<?> type : instrumentation.getAllLoadedClasses()) {
                if (type.getName().equals("net.minecraft.client.Minecraft")) { minecraftClass = type; break; }
            }
            if (minecraftClass == null) throw new IllegalStateException("Minecraft class is not loaded");
            Object minecraft = callStatic(minecraftClass, "getInstance");
            execute(minecraft, () -> {
                try {
                    Object player = field(minecraft, "player");
                    line("client.thread=" + Thread.currentThread().getName());
                    describeEntity("client.player", player);
                    Object camera = call(minecraft, "getCameraEntity");
                    line("client.cameraIsPlayer=" + (camera == player));
                    line("client.cameraClass=" + (camera == null ? "null" : camera.getClass().getName()));
                    value("client.player.isControlledCamera", () -> call(player, "isControlledCamera"));
                    Object connection = player == null ? null : field(player, "connection");
                    value("client.connection.hasClientLoaded", () -> call(connection, "hasClientLoaded"));
                    value("client.connection.clientLoaded", () -> field(connection, "clientLoaded"));
                    Object uuid = player == null ? null : call(player, "getUUID");
                    Object server = call(minecraft, "getSingleplayerServer");
                    if (server == null) { line("server=null"); done.countDown(); return; }
                    execute(server, () -> {
                        try {
                            line("server.thread=" + Thread.currentThread().getName());
                            Object players = call(call(server, "getPlayerList"), "getPlayers");
                            int count = 0;
                            for (Object serverPlayer : (Iterable<?>) players) {
                                String prefix = "server.player[" + count++ + "]";
                                describeEntity(prefix, serverPlayer);
                                value(prefix + ".matchesClientUUID", () -> call(serverPlayer, "getUUID").equals(uuid));
                                value(prefix + ".wonGame", () -> field(serverPlayer, "wonGame"));
                                Object sc = field(serverPlayer, "connection");
                                value(prefix + ".connection.hasClientLoaded", () -> call(sc, "hasClientLoaded"));
                                for (String name : new String[]{"awaitingPositionFromClient", "awaitingTeleport", "awaitingTeleportTime", "waitingForRespawn", "clientLoadedTimeoutTimer", "tickCount", "receivedPositionThisTick", "firstGoodX", "firstGoodY", "firstGoodZ", "lastGoodX", "lastGoodY", "lastGoodZ"}) {
                                    value(prefix + ".connection." + name, () -> field(sc, name));
                                }
                            }
                            line("server.playerCount=" + count);
                        } catch (Throwable error) { failure("server", error); }
                        finally { done.countDown(); }
                    });
                } catch (Throwable error) { failure("client", error); done.countDown(); }
            });
            line("scheduled=true");
            line("finished=" + done.await(12, TimeUnit.SECONDS));
        } catch (Throwable error) { failure("agent", error); }
        line("END read-only probe");
    }

    private static void describeEntity(String prefix, Object entity) {
        if (entity == null) { line(prefix + "=null"); return; }
        value(prefix + ".uuid", () -> call(entity, "getUUID"));
        value(prefix + ".pos", () -> call(entity, "position"));
        value(prefix + ".isPassenger", () -> call(entity, "isPassenger"));
        value(prefix + ".isAlive", () -> call(entity, "isAlive"));
        value(prefix + ".noPhysics", () -> field(entity, "noPhysics"));
    }

    @FunctionalInterface private interface Read { Object get() throws Exception; }
    private static void value(String key, Read read) {
        try { line(key + "=" + read.get()); }
        catch (Throwable error) { line(key + "=<" + error.getClass().getSimpleName() + ">"); }
    }

    private static Object field(Object target, String name) throws Exception {
        if (target == null) return null;
        for (Class<?> type = target.getClass(); type != null; type = type.getSuperclass()) {
            try { Field f = type.getDeclaredField(name); f.setAccessible(true); return f.get(target); }
            catch (NoSuchFieldException ignored) { }
        }
        throw new NoSuchFieldException(name);
    }

    private static Object call(Object target, String name) throws Exception {
        if (target == null) return null;
        Method method = method(target.getClass(), name);
        return method.invoke(target);
    }
    private static Object callStatic(Class<?> type, String name) throws Exception {
        return method(type, name).invoke(null);
    }
    private static Method method(Class<?> type, String name, Class<?>... args) throws Exception {
        for (Class<?> current = type; current != null; current = current.getSuperclass()) {
            try { Method m = current.getDeclaredMethod(name, args); m.setAccessible(true); return m; }
            catch (NoSuchMethodException ignored) { }
        }
        throw new NoSuchMethodException(name);
    }
    private static void execute(Object owner, Runnable runnable) throws Exception {
        method(owner.getClass(), "execute", Runnable.class).invoke(owner, runnable);
    }
    private static void failure(String section, Throwable error) {
        Throwable cause = error;
        while (cause.getCause() != null) cause = cause.getCause();
        line(section + ".error=" + cause.getClass().getName() + ": " + cause.getMessage());
    }
    private static synchronized void line(String text) {
        try {
            Files.writeString(LOG, Instant.now() + " " + text + System.lineSeparator(), StandardOpenOption.CREATE, StandardOpenOption.APPEND);
        } catch (Exception error) { System.err.println("FluidLiveProbe log error: " + error.getClass().getSimpleName()); }
    }
}
