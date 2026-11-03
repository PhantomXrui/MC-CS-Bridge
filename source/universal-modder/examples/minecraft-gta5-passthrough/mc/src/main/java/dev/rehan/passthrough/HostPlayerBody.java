package dev.rehan.passthrough;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import java.util.UUID;
import java.util.Map;
import java.util.WeakHashMap;
import net.minecraft.world.entity.player.Player;

/** Optional authoritative host body; independent of the render camera and MC packet latency. */
public final class HostPlayerBody {
    public record Bounds(double minX, double minY, double minZ, double maxX, double maxY, double maxZ) {
        /** Face contact is legal. Only positive volume overlap prevents placement. */
        public boolean intersects(double x0, double y0, double z0, double x1, double y1, double z1) {
            return maxX > x0 && minX < x1 && maxY > y0 && minY < y1 && maxZ > z0 && minZ < z1;
        }
    }
    public record Snapshot(double x, double y, double z, Bounds bounds, boolean sneak, boolean grounded, float eyeHeight, long receivedNanos) {}
    /** Host starts a session once; its subsequent poses are never movement commands. */
    public record Movement(long session, double x, double y, double z, long receivedNanos) {}
    private static volatile Snapshot latest;
    private static volatile Movement movement;
    private static volatile long stoppedSession;
    private static volatile UUID localPlayer;
    private record Flight(long session, boolean mayfly, boolean flying) {}
    private static final Map<Player,Flight> suspendedFlight = new WeakHashMap<>();
    private HostPlayerBody() {}

    /** body = relative MC-coordinate AABB, pl = feet. Older hosts retain vanilla behaviour. */
    public static void receive(JsonObject message) {
        receiveMovement(message);
        // Minecraft owns its actual body/eye/pose while walking natively. Returning
        // no host body lets all vanilla placement and ray rules run unchanged.
        if (movement != null) { latest=null; return; }
        if (!message.has("body") || !message.has("pl") || !message.has("sneak")
            || (message.has("drive") && message.get("drive").getAsBoolean())) { latest = null; return; }
        JsonArray feet = message.getAsJsonArray("pl"), body = message.getAsJsonArray("body");
        if (feet.size() != 3 || body.size() != 6) { latest = null; return; }
        double x=feet.get(0).getAsDouble(), y=feet.get(1).getAsDouble(), z=feet.get(2).getAsDouble();
        double[] b = new double[6];
        for (int i=0;i<6;i++) { b[i]=body.get(i).getAsDouble(); if (!Double.isFinite(b[i])) { latest=null; return; } }
        if (!Double.isFinite(x) || !Double.isFinite(y) || !Double.isFinite(z)
            || b[3]<=b[0] || b[4]<=b[1] || b[5]<=b[2]
            || b[3]-b[0]>4 || b[4]-b[1]>4 || b[5]-b[2]>4) { latest=null; return; }
        float eye=message.has("eye")?message.get("eye").getAsFloat():Float.NaN;
        if (!Float.isFinite(eye) || eye<=0 || eye>4) eye=Float.NaN;
        latest = new Snapshot(x,y,z,new Bounds(x+b[0],y+b[1],z+b[2],x+b[3],y+b[4],z+b[5]),
            message.get("sneak").getAsBoolean(), message.has("grounded") && message.get("grounded").getAsBoolean(), eye, System.nanoTime());
    }

    /** Integrated client/server copies share this UUID; host bot proxies never enter this path. */
    public static void bind(Player player) { localPlayer=player.getUUID(); }
    public static Snapshot currentFor(Player player) {
        return player.getUUID().equals(localPlayer)?current():null;
    }

    public static Snapshot current() {
        if (minecraftMovement()) return null;
        Snapshot snapshot=latest;
        return Passthrough.active && snapshot!=null && System.nanoTime()-snapshot.receivedNanos()<2_000_000_000L ? snapshot : null;
    }

    private static void receiveMovement(JsonObject message) {
        if (!message.has("mcMove") || !message.get("mcMove").getAsBoolean()) { movement=null; return; }
        long session=message.has("moveSession")?message.get("moveSession").getAsLong():0;
        Movement previous=movement;
        if (session<=0 || session==stoppedSession) { movement=null; return; }
        double x,y,z;
        if (message.has("anchor")) {
            JsonArray at=message.getAsJsonArray("anchor");
            if (at.size()!=3) { movement=null; return; }
            x=at.get(0).getAsDouble();y=at.get(1).getAsDouble();z=at.get(2).getAsDouble();
        } else if (previous!=null && previous.session()==session) {
            x=previous.x();y=previous.y();z=previous.z();
        } else { movement=null; return; }
        if (!Double.isFinite(x)||!Double.isFinite(y)||!Double.isFinite(z)) { movement=null; return; }
        // Do not let a changing camera accidentally re-anchor a live session.
        if (previous!=null && previous.session()==session) { x=previous.x();y=previous.y();z=previous.z(); }
        movement=new Movement(session,x,y,z,System.nanoTime());
    }

    public static Movement movement() {
        Movement request=movement;
        return Passthrough.active && request!=null && System.nanoTime()-request.receivedNanos()<2_000_000_000L?request:null;
    }
    public static boolean minecraftMovement() { return movement()!=null; }
    public static boolean minecraftMovement(Player player) { return player.getUUID().equals(localPlayer) && minecraftMovement(); }

    /** A late cam from a stopped lease must not silently resume movement. */
    public static void stopMovement(long session) {
        Movement request=movement;
        if (request!=null && request.session()==session) { stoppedSession=session;movement=null;latest=null; }
    }
    public static long movementAgeMillis() {
        Movement request=movement;
        return request==null?-1:(System.nanoTime()-request.receivedNanos())/1_000_000L;
    }

    /** Drop legacy forced flight once per session; vanilla controls every later flight transition. */
    public static void updateMovementAbilities(Player player) {
        boolean changed=false, initialize=false;
        synchronized (suspendedFlight) {
            var abilities=player.getAbilities();
            if (minecraftMovement(player)) {
                var request=movement();
                if (request==null) return;
                Flight saved=suspendedFlight.get(player);
                if (saved==null || saved.session()!=request.session()) {
                    suspendedFlight.put(player,new Flight(request.session(),saved==null?abilities.mayfly:saved.mayfly(),saved==null?abilities.flying:saved.flying()));
                    initialize=true;
                    if (abilities.flying) {abilities.flying=false;changed=true;}
                }
            } else {
                Flight saved=suspendedFlight.remove(player);
                if (saved!=null) { abilities.mayfly=saved.mayfly();abilities.flying=saved.flying();changed=true; }
            }
        }
        if (initialize) player.stopFallFlying();
        if (changed) player.onUpdateAbilities();
    }
}
