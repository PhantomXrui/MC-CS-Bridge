import com.sun.tools.attach.VirtualMachine;
import java.io.File;
import java.lang.instrument.Instrumentation;
import java.lang.invoke.MethodHandles;
import java.nio.file.*;
import java.security.MessageDigest;
import java.util.HexFormat;

/** Project-scoped live installation of a new event handler, without restarting the world. */
public final class MemeBreedingAgent {
    static final Path ROOT=Path.of("D:/MC-CS-Bridge");
    static final Path WORK=ROOT.resolve("build-cache/meme-breeding");
    static void log(String message) {
        try { Files.writeString(WORK.resolve("live-status.txt"),java.time.Instant.now()+" "+message+"\n",StandardOpenOption.CREATE,StandardOpenOption.APPEND); }
        catch(Exception error) { throw new RuntimeException(error); }
    }
    public static void main(String[] args) throws Exception {
        if(args.length!=1 || !args[0].matches("[0-9]+"))throw new IllegalArgumentException("PID required");
        var vm=VirtualMachine.attach(args[0]);
        try {
            if(!Files.isSameFile(Path.of(vm.getSystemProperties().getProperty("user.dir")),ROOT.resolve("minecraft")))throw new IllegalStateException("Wrong JVM");
            vm.loadAgent(WORK.resolve("meme-breeding-agent.jar").toString(),"");
        } finally { vm.detach(); }
    }
    public static void agentmain(String args,Instrumentation instrumentation) throws Exception {
        Class<?> host=null,client=null;
        for(var type:instrumentation.getAllLoadedClasses()) {
            if(type.getName().equals("dev.rehan.passthrough.Passthrough"))host=type;
            if(type.getName().equals("net.minecraft.client.Minecraft"))client=type;
        }
        if(host==null || client==null)throw new IllegalStateException("Expected project classes not loaded");
        Object mc=client.getMethod("getInstance").invoke(null);
        if(!Files.isSameFile(((File)client.getField("gameDirectory").get(mc)).toPath(),ROOT.resolve("minecraft")))throw new IllegalStateException("Wrong game directory");
        var lookup=MethodHandles.privateLookupIn(host,MethodHandles.lookup());
        for(String name:new String[]{"MemeBreeding", "MemeBreeding$State"}) {
            Path file=ROOT.resolve("runtime/patches/meme-breeding/"+name+".class");
            byte[] bytes=Files.readAllBytes(file);
            String expected=Files.readString(file.resolveSibling(name+".sha256")).trim();
            if(!HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes)).equals(expected))throw new IllegalStateException("Patch hash mismatch");
            try { Class.forName("dev.rehan.passthrough."+name,false,host.getClassLoader()); }
            catch(ClassNotFoundException absent) { lookup.defineClass(bytes); }
        }
        Class<?> helper=Class.forName("dev.rehan.passthrough.MemeBreeding",true,host.getClassLoader());
        Object server=client.getMethod("getSingleplayerServer").invoke(mc);
        if(server==null)throw new IllegalStateException("No local world");
        server.getClass().getMethod("execute",Runnable.class).invoke(server,(Runnable)()->{
            try { helper.getMethod("initialize").invoke(null);log("initialized pid="+ProcessHandle.current().pid()); }
            catch(Exception error) { log("failed "+error);error.printStackTrace(); }
        });
    }
}
