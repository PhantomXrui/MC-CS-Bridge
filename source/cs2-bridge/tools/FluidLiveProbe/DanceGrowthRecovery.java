import com.sun.tools.attach.VirtualMachine;
import java.lang.instrument.*;
import java.lang.invoke.MethodHandles;
import java.lang.reflect.*;
import java.nio.file.*;
import java.security.ProtectionDomain;
import java.time.Instant;
import java.util.*;
import java.util.concurrent.*;
import java.util.jar.JarFile;
import org.objectweb.asm.*;
import org.objectweb.asm.tree.*;

/** Narrow live recovery. No process/thread termination, no edits to blocks/entities. */
public final class DanceGrowthRecovery {
    static final Path ROOT=Path.of("D:\\MC-CS-Bridge");
    static final Path HERE=ROOT.resolve("source/cs2-bridge/tools/FluidLiveProbe");
    static final Path LOG=ROOT.resolve("logs/dance-growth-recovery.txt");
    static final String COLL="net/minecraft/world/level/CollisionGetter";
    static final String BITS="net/minecraft/world/phys/shapes/BitSetDiscreteVoxelShape";
    static final String SHAPES="net/minecraft/world/phys/shapes/Shapes";
    static final String VS="Lnet/minecraft/world/phys/shapes/VoxelShape;";
    static final String FREE="(Lnet/minecraft/world/entity/Entity;"+VS+"Lnet/minecraft/world/phys/Vec3;DDD)Ljava/util/Optional;";
    static volatile boolean temporary;
    static Class<?> gate;
    static ClassFileTransformer transformer;
    static Class<?> collision,bits,shapes;
    public static void main(String[] args)throws Exception {
        if(args.length<2)throw new IllegalArgumentException("PID agent.jar [recover|restore]");
        VirtualMachine vm=VirtualMachine.attach(args[0]);
        try{vm.loadAgent(Path.of(args[1]).toAbsolutePath().toString(),args.length>2?args[2]:"recover");}finally{vm.detach();}
        System.out.println("Recovery log: "+LOG);
    }
    public static void agentmain(String mode,Instrumentation inst)throws Exception {
        try {
            if(mode.equals("restore")){
                if(transformer==null)throw new IllegalStateException("No recovery transformer active");
                temporary=false;gate.getField("enabled").setBoolean(null,false);
                inst.retransformClasses(bits,shapes);
                line("RESTORED transient Shapes and BitSet callbacks; bounded HostFreePosition remains until JVM exit");return;
            }
            if(transformer!=null)throw new IllegalStateException("Recovery already installed; use restore");
            if(!inst.isRetransformClassesSupported())throw new IllegalStateException("Retransform unsupported");
            Thread stuck=null;
            for(var entry:Thread.getAllStackTraces().entrySet()){
                boolean free=false,chicken=false,join=false;
                for(var frame:entry.getValue()){
                    if(frame.getClassName().equals(COLL.replace('/','.'))&&frame.getMethodName().equals("findFreePosition"))free=true;
                    if(frame.getClassName().equals("net.minecraft.world.entity.animal.chicken.Chicken")&&frame.getMethodName().equals("aiStep"))chicken=true;
                    if(frame.getClassName().equals(BITS.replace('/','.')))join=true;
                }
                if(entry.getKey().getName().equals("Server thread")&&free&&chicken&&join)stuck=entry.getKey();
            }
            if(stuck==null)throw new IllegalStateException("Observed chicken-growth union stack absent; refused mutation");
            Class<?> terrain=null,mcType=null;
            for(Class<?> type:inst.getAllLoadedClasses()){
                switch(type.getName()){
                    case "dev.rehan.passthrough.HostTerrain" -> terrain=type;
                    case "net.minecraft.client.Minecraft" -> mcType=type;
                    case "net.minecraft.world.level.CollisionGetter" -> collision=type;
                    case "net.minecraft.world.phys.shapes.BitSetDiscreteVoxelShape" -> bits=type;
                    case "net.minecraft.world.phys.shapes.Shapes" -> shapes=type;
                }
            }
            if(terrain==null||mcType==null||collision==null||bits==null||shapes==null)throw new IllegalStateException("Required loaded classes absent");
            for(Class<?> type:new Class<?>[]{collision,bits,shapes})if(!inst.isModifiableClass(type))throw new IllegalStateException("Class unmodifiable: "+type);
            for(String part:new String[]{"asm","asm-tree"})inst.appendToSystemClassLoaderSearch(new JarFile(ROOT.resolve("minecraft/libraries/org/ow2/asm/"+part+"/9.10.1/"+part+"-9.10.1.jar").toFile()));
            inst.appendToBootstrapClassLoaderSearch(new JarFile(HERE.resolve("dance-growth-gate.jar").toFile()));
            gate=Class.forName("DanceGrowthRecoveryGate",true,null);
            gate.getField("serverId").setLong(null,stuck.threadId());
            // This class is the tested permanent implementation, defined into the same mod loader.
            byte[] helper=Files.readAllBytes(ROOT.resolve("source/universal-modder/examples/minecraft-gta5-passthrough/mc/build/classes/java/main/dev/rehan/passthrough/HostFreePosition.class"));
            try{Class.forName("dev.rehan.passthrough.HostFreePosition",false,terrain.getClassLoader());}
            catch(ClassNotFoundException absent){MethodHandles.privateLookupIn(terrain,MethodHandles.lookup()).defineClass(helper);}
            transformer=new ClassFileTransformer(){
                @Override public byte[] transform(ClassLoader loader,String name,Class<?> redef,ProtectionDomain domain,byte[] input){
                    if(!COLL.equals(name)&&!(temporary&&(BITS.equals(name)||SHAPES.equals(name))))return null;
                    try{return patch(name,input);}catch(Throwable failure){line("TRANSFORM ERROR "+name+": "+failure);throw new IllegalStateException(failure);}
                }
            };
            temporary=true;gate.getField("enabled").setBoolean(null,true);
            inst.addTransformer(transformer,true);
            // Install prevention before unblocking the old invocation.
            inst.retransformClasses(collision);
            line("Installed bounded free-position guard");
            inst.retransformClasses(bits,shapes);
            line("Installed stack-gated recovery on Server thread id="+stuck.threadId());
            Object mc=mcType.getMethod("getInstance").invoke(null);
            Object server=mcType.getMethod("getSingleplayerServer").invoke(mc);
            CountDownLatch checkpoint=new CountDownLatch(1);
            method(server.getClass(),"execute",Runnable.class).invoke(server,(Runnable)()->{
                line("SERVER QUEUE RESUMED; no world content altered by recovery; ready for cleanup/save");checkpoint.countDown();
            });
            line("checkpointWithin20s="+checkpoint.await(20,TimeUnit.SECONDS));
        }catch(Throwable failure){line("ERROR "+failure);throw failure;}
    }
    static byte[] patch(String name,byte[] input){
        var reader=new ClassReader(input);var node=new ClassNode(Opcodes.ASM9);reader.accept(node,0);
        int count=0;
        for(var method:node.methods){
            boolean free=name.equals(COLL)&&method.name.equals("findFreePosition")&&method.desc.equals(FREE);
            boolean callback=name.equals(BITS)&&method.name.equals("lambda$join$2")&&method.desc.endsWith(")Z");
            boolean join=name.equals(SHAPES)&&method.name.equals("joinUnoptimized")&&method.desc.equals("("+VS+VS+"Lnet/minecraft/world/phys/shapes/BooleanOp;)"+VS);
            if(!free&&!callback&&!join)continue;
            InsnList code=new InsnList();LabelNode resume=new LabelNode();
            if(free){
                code.add(new VarInsnNode(Opcodes.ALOAD,1));
                code.add(new MethodInsnNode(Opcodes.INVOKESTATIC,"dev/rehan/passthrough/HostTerrain","movementEntity","(Lnet/minecraft/world/entity/Entity;)Z",false));
                code.add(new JumpInsnNode(Opcodes.IFEQ,resume));
                for(int i=0;i<4;i++)code.add(new VarInsnNode(Opcodes.ALOAD,i));
                for(int i=4;i<10;i+=2)code.add(new VarInsnNode(Opcodes.DLOAD,i));
                code.add(new MethodInsnNode(Opcodes.INVOKESTATIC,"dev/rehan/passthrough/HostFreePosition","find","(L"+COLL+";"+FREE.substring(1),false));
                code.add(new InsnNode(Opcodes.ARETURN));
            }else{
                code.add(new MethodInsnNode(Opcodes.INVOKESTATIC,"DanceGrowthRecoveryGate","shouldAbort","()Z",false));
                code.add(new JumpInsnNode(Opcodes.IFEQ,resume));
                if(callback){code.add(new InsnNode(Opcodes.ICONST_0));code.add(new InsnNode(Opcodes.IRETURN));}
                else {code.add(new MethodInsnNode(Opcodes.INVOKESTATIC,SHAPES,"empty","()"+VS,false));code.add(new InsnNode(Opcodes.ARETURN));}
            }
            code.add(resume);code.add(new FrameNode(Opcodes.F_SAME,0,null,0,null));method.instructions.insert(code);count++;
        }
        if(count!=1)throw new IllegalStateException("Unexpected matching method count "+count+" for "+name);
        var writer=new ClassWriter(ClassWriter.COMPUTE_MAXS);node.accept(writer);return writer.toByteArray();
    }
    static Method method(Class<?> type,String name,Class<?>... args)throws Exception{
        for(Class<?> at=type;at!=null;at=at.getSuperclass())try{var m=at.getDeclaredMethod(name,args);m.setAccessible(true);return m;}catch(NoSuchMethodException ignored){}
        throw new NoSuchMethodException(name);
    }
    static synchronized void line(String text){try{Files.writeString(LOG,Instant.now()+" "+text+System.lineSeparator(),StandardOpenOption.CREATE,StandardOpenOption.APPEND);}catch(Exception failure){System.err.println(failure);}}
}
