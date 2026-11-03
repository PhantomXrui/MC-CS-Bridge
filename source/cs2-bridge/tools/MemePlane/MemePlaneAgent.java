import com.sun.tools.attach.VirtualMachine;
import java.io.File;
import java.lang.instrument.ClassDefinition;
import java.lang.instrument.Instrumentation;
import java.nio.file.*;
import java.security.MessageDigest;
import java.util.HexFormat;
import java.util.concurrent.CompletableFuture;

/** One-shot, project-scoped method update and resource reload; never stops or clears the world. */
public final class MemePlaneAgent {
    static final Path ROOT=Path.of("D:/MC-CS-Bridge");
    static final Path WORK=ROOT.resolve("build-cache/meme-plane");
    static void log(String message) {
        try { Files.writeString(WORK.resolve("live-status.txt"),java.time.Instant.now()+" "+message+"\n",StandardOpenOption.CREATE,StandardOpenOption.APPEND); }
        catch(Exception error) { throw new RuntimeException(error); }
    }
    public static void main(String[] args) throws Exception {
        if(args.length!=2 || !args[0].matches("[0-9]+") || !args[1].matches("[a-f0-9]{64}"))throw new IllegalArgumentException("PID and patch SHA256 required");
        var vm=VirtualMachine.attach(args[0]);
        try {
            if(!Files.isSameFile(Path.of(vm.getSystemProperties().getProperty("user.dir")),ROOT.resolve("minecraft")))throw new IllegalStateException("Wrong JVM");
            vm.loadAgent(WORK.resolve("meme-plane-agent.jar").toString(),args[1]);
        } finally { vm.detach(); }
    }
    public static void agentmain(String hash,Instrumentation instrumentation) throws Exception {
        Class<?> target=null,client=null;
        for(var type:instrumentation.getAllLoadedClasses()) {
            if(type.getName().equals("dev.rehan.passthrough.client.SquashDance"))target=type;
            if(type.getName().equals("net.minecraft.client.Minecraft"))client=type;
        }
        if(target==null || client==null)throw new IllegalStateException("Expected project classes not loaded");
        Object mc=client.getMethod("getInstance").invoke(null);
        if(!Files.isSameFile(((File)client.getField("gameDirectory").get(mc)).toPath(),ROOT.resolve("minecraft")))throw new IllegalStateException("Wrong game directory");
        byte[] patch=Files.readAllBytes(ROOT.resolve("runtime/patches/meme-plane/SquashDance.class"));
        if(!HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(patch)).equals(hash))throw new IllegalStateException("Patch hash mismatch");
        instrumentation.redefineClasses(new ClassDefinition(target,patch));
        log("class-update-ok sha256="+hash+" pid="+ProcessHandle.current().pid());
        Class<?> mcClass=client;
        client.getMethod("execute",Runnable.class).invoke(mc,(Runnable)()->{
            try {
                var future=(CompletableFuture<?>)mcClass.getMethod("reloadResourcePacks").invoke(mc);
                future.whenComplete((unused,error)->log(error==null?"resource-reload-ok":"resource-reload-failed "+error));
            } catch(Exception error) { log("resource-reload-failed "+error); }
        });
    }
}
