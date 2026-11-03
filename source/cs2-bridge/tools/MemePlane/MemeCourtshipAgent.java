import com.sun.tools.attach.VirtualMachine;
import java.lang.instrument.*;
import java.nio.file.*;
import java.security.MessageDigest;
import java.util.*;

public final class MemeCourtshipAgent {
    static final Path ROOT=Path.of("D:/MC-CS-Bridge");
    static final Path WORK=ROOT.resolve("build-cache/meme-breeding");
    public static void main(String[] args) throws Exception {
        if(args.length!=1 || !args[0].matches("[0-9]+"))throw new IllegalArgumentException("PID required");
        var vm=VirtualMachine.attach(args[0]);
        try {
            if(!Files.isSameFile(Path.of(vm.getSystemProperties().getProperty("user.dir")),ROOT.resolve("minecraft")))throw new IllegalStateException("Wrong JVM");
            vm.loadAgent(WORK.resolve("meme-courtship-agent.jar").toString(),"");
        } finally { vm.detach(); }
    }
    public static void agentmain(String args,Instrumentation inst) throws Exception {
        var definitions=new ArrayList<ClassDefinition>();
        for(String name:new String[]{"MemeBreeding"}) {
            Class<?> target=null;
            for(var type:inst.getAllLoadedClasses())if(type.getName().equals("dev.rehan.passthrough."+name))target=type;
            if(target==null)throw new IllegalStateException("Missing "+name);
            Path file=ROOT.resolve("runtime/patches/meme-breeding/"+name+".class");
            byte[] bytes=Files.readAllBytes(file);
            if(!HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes)).equals(Files.readString(file.resolveSibling(name+".sha256")).trim()))throw new IllegalStateException("Checksum mismatch");
            definitions.add(new ClassDefinition(target,bytes));
        }
        inst.redefineClasses(definitions.toArray(ClassDefinition[]::new));
        Files.writeString(WORK.resolve("courtship-live-status.txt"),"courtship exact rays and placed-block collision enabled; pid="+ProcessHandle.current().pid());
    }
}
