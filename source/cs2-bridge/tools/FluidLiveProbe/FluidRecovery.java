import com.sun.tools.attach.VirtualMachine;
import java.lang.instrument.ClassDefinition;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.time.Instant;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/** One-shot recovery of the observed 26.3 half-resurrected local player, plus a mod-only hotfix. */
public final class FluidRecovery {
    private static final Path ROOT=Path.of("D:\\MC-CS-Bridge");
    private static final Path LOG=ROOT.resolve("logs/fluid-recovery.txt");
    public static void main(String[] args)throws Exception{
        if(args.length!=2)throw new IllegalArgumentException("Expected PID and this agent JAR");
        VirtualMachine vm=VirtualMachine.attach(args[0]);
        try{vm.loadAgent(Path.of(args[1]).toAbsolutePath().toString(),"");}finally{vm.detach();}
        System.out.println("Recovery result: "+LOG);
    }
    public static void agentmain(String ignored,Instrumentation instrumentation){
        CountDownLatch done=new CountDownLatch(1);
        try{
            Class<?> mcClass=null,vitals=null;
            for(Class<?> c:instrumentation.getAllLoadedClasses()){
                if(c.getName().equals("net.minecraft.client.Minecraft"))mcClass=c;
                if(c.getName().equals("dev.rehan.passthrough.HostVitals"))vitals=c;
            }
            if(mcClass==null||vitals==null)throw new IllegalStateException("Expected classes absent");
            // Only our untransformed mod class changes, with the exact same fields and methods.
            byte[] patch=Files.readAllBytes(ROOT.resolve("source/universal-modder/examples/minecraft-gta5-passthrough/mc/build/classes/java/main/dev/rehan/passthrough/HostVitals.class"));
            instrumentation.redefineClasses(new ClassDefinition(vitals,patch));
            line("HostVitals guard hot-redefined");
            Object mc=method(mcClass,"getInstance").invoke(null);
            execute(mc,()->{
                try{
                    Object cp=field(mc,"player"),cc=field(cp,"connection");
                    Object uuid=call(cp,"getUUID");
                    boolean loaded=(Boolean)call(cc,"hasClientLoaded");
                    Object server=call(mc,"getSingleplayerServer");
                    if(server==null||!loaded)throw new IllegalStateException("No loaded integrated client");
                    line("client.uuid="+uuid+" pos="+call(cp,"position"));
                    execute(server,()->{
                        try{
                            for(Object p:(Iterable<?>)call(call(server,"getPlayerList"),"getPlayers")){
                                if(!call(p,"getUUID").equals(uuid))continue;
                                Object c=field(p,"connection");
                                boolean waiting=(Boolean)field(c,"waitingForRespawn");
                                int timer=(Integer)field(c,"clientLoadedTimeoutTimer");
                                boolean alive=(Boolean)call(p,"isAlive");
                                boolean dead=(Boolean)field(p,"dead");
                                int deathTime=(Integer)field(p,"deathTime");
                                line("server.pos="+call(p,"position")+" waiting="+waiting+" timer="+timer+" alive="+alive+" dead="+dead+" deathTime="+deathTime+" health="+call(p,"getHealth"));
                                if(!waiting){line("No stuck respawn gate; recovery skipped");return;}
                                if(timer!=0||!alive||dead||deathTime!=0||(Boolean)call(p,"isPassenger")||field(c,"awaitingPositionFromClient")!=null)
                                    throw new IllegalStateException("Not the known half-resurrected state; refused recovery");
                                // Use vanilla's respawn/load transition; no direct writes to connection fields.
                                call(c,"restartClientLoadTimerAfterRespawn");
                                line("Recovered via vanilla restartClientLoadTimerAfterRespawn; waiting="+field(c,"waitingForRespawn")+" timer="+field(c,"clientLoadedTimeoutTimer"));
                                return;
                            }
                            throw new IllegalStateException("Local player absent");
                        }catch(Throwable e){failure(e);}finally{done.countDown();}
                    });
                }catch(Throwable e){failure(e);done.countDown();}
            });
            line("completed="+done.await(12,TimeUnit.SECONDS));
        }catch(Throwable e){failure(e);}
    }
    private static Object field(Object target,String name)throws Exception{
        for(Class<?> c=target.getClass();c!=null;c=c.getSuperclass()){
            try{Field f=c.getDeclaredField(name);f.setAccessible(true);return f.get(target);}catch(NoSuchFieldException ignored){}
        }
        throw new NoSuchFieldException(name);
    }
    private static Method method(Class<?> type,String name,Class<?>...args)throws Exception{
        for(Class<?> c=type;c!=null;c=c.getSuperclass()){
            try{Method m=c.getDeclaredMethod(name,args);m.setAccessible(true);return m;}catch(NoSuchMethodException ignored){}
        }
        throw new NoSuchMethodException(name);
    }
    private static Object call(Object target,String name)throws Exception{return method(target.getClass(),name).invoke(target);}
    private static void execute(Object target,Runnable action)throws Exception{method(target.getClass(),"execute",Runnable.class).invoke(target,action);}
    private static void failure(Throwable e){while(e.getCause()!=null)e=e.getCause();line("ERROR "+e);}
    private static synchronized void line(String text){
        try{Files.writeString(LOG,Instant.now()+" "+text+System.lineSeparator(),StandardOpenOption.CREATE,StandardOpenOption.APPEND);}catch(Exception e){System.err.println(e);}
    }
}
