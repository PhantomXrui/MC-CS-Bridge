/** Only the currently observed chicken-growth free-position stack may be shortened. */
public final class DanceGrowthRecoveryGate {
    public static volatile boolean enabled;
    public static volatile long serverId;
    public static boolean shouldAbort() {
        Thread thread=Thread.currentThread();
        if(!enabled||thread.threadId()!=serverId)return false;
        boolean free=false,chicken=false;
        for(StackTraceElement frame:thread.getStackTrace()) {
            if(frame.getClassName().equals("net.minecraft.world.level.CollisionGetter")&&frame.getMethodName().equals("findFreePosition"))free=true;
            if(frame.getClassName().equals("net.minecraft.world.entity.animal.chicken.Chicken")&&frame.getMethodName().equals("aiStep"))chicken=true;
        }
        return free&&chicken;
    }
}
