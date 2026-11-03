package dev.rehan.passthrough.client;

import java.lang.foreign.ValueLayout;
import java.lang.invoke.VarHandle;

/** Optional CS-local render snapshot. JSON remains the control/simulation fallback. */
final class HostRenderPose {
    private static SharedMemory memory;
    private static long nextOpen, lastSequence, changedAt;
    // The local A/B run did not improve camera freshness; keep this experimental.
    static boolean enabled = Boolean.getBoolean("passthrough.sharedRenderPose");
    static boolean used;
    static HostState.Pose read(HostState.Pose fallback) {
        used = false;
        if (!enabled || fallback == null || fallback.drive()) return fallback;
        long now = System.nanoTime();
        try {
            if (memory == null && now >= nextOpen) {
                nextOpen = now + 1_000_000_000L;
                memory = SharedMemory.open("Local\\MC_CS_Bridge_Pose",4096);
            }
            if (memory == null) return fallback;
            var m = memory.segment;
            long seq = m.get(ValueLayout.JAVA_LONG,1792);
            VarHandle.acquireFence();
            if ((seq & 1)!=0 || seq==0 || m.get(ValueLayout.JAVA_INT,1800)!=0x50525343 || m.get(ValueLayout.JAVA_INT,1804)==0) return fallback;
            long host = m.get(ValueLayout.JAVA_LONG,1808);
            float[] p = new float[9];
            for (int i=0;i<9;i++) p[i]=m.get(ValueLayout.JAVA_FLOAT,1816+i*4);
            float lookYaw=m.get(ValueLayout.JAVA_FLOAT,1852),lookPitch=m.get(ValueLayout.JAVA_FLOAT,1856);
            int mode=m.get(ValueLayout.JAVA_INT,1860);
            VarHandle.acquireFence();
            if (seq!=m.get(ValueLayout.JAVA_LONG,1792) || Math.abs(host-fallback.hostFrame())>8) return fallback;
            if (seq!=lastSequence) { lastSequence=seq;changedAt=now; }
            if (now-changedAt>250_000_000L) return fallback;
            for (float f:p) if (!Float.isFinite(f)) return fallback;
            if (p[5]<10 || p[5]>160 || mode<0 || mode>2) return fallback;
            used=true;
            return new HostState.Pose(host,p[0],p[1],p[2],p[3],p[4],0,p[5],mode==0,
                p[6],p[7],p[8],lookYaw,now,false,lookYaw,lookPitch,false,mode==2,true);
        } catch (Throwable error) { nextOpen=now+1_000_000_000L;return fallback; }
    }
}
