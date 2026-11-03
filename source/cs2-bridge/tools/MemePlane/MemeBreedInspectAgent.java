import com.sun.tools.attach.VirtualMachine;
import java.lang.instrument.Instrumentation;
import java.nio.file.*;
import java.util.*;
public class MemeBreedInspectAgent {
  static Path root=Path.of("D:/MC-CS-Bridge");
  public static void main(String[] a)throws Exception {
    var vm=VirtualMachine.attach(a[0]);try {
      if(!Files.isSameFile(Path.of(vm.getSystemProperties().getProperty("user.dir")),root.resolve("minecraft")))throw new IllegalStateException();
      vm.loadAgent(root.resolve("build-cache/meme-breeding/inspect.jar").toString());
    } finally {vm.detach();}
  }
  public static void agentmain(String a,Instrumentation inst)throws Exception {
    ClassLoader loader=null;
    for(var c:inst.getAllLoadedClasses())if(c.getName().equals("dev.rehan.passthrough.MemeBreeding"))loader=c.getClassLoader();
    final ClassLoader cl=loader;
    var mcC=Class.forName("net.minecraft.client.Minecraft",true,cl);var mc=mcC.getMethod("getInstance").invoke(null);
    var server=mcC.getMethod("getSingleplayerServer").invoke(mc);
    server.getClass().getMethod("execute",Runnable.class).invoke(server,(Runnable)()->{try{
      var h=Class.forName("dev.rehan.passthrough.MemeBreeding",true,cl);var field=h.getDeclaredField("fed");field.setAccessible(true);
      var map=(Map<?,?>)field.get(null);var out=new StringBuilder("fed="+map.size()+"\n");
      var v=Class.forName("net.minecraft.world.phys.Vec3",true,cl);var terrain=Class.forName("dev.rehan.passthrough.HostTerrain",true,cl);
      for(var e:map.entrySet()){
        var cow=e.getKey();var cc=cow.getClass();var p=cc.getMethod("position").invoke(cow);
        out.append("id="+cc.getMethod("getId").invoke(cow)+" pos="+p);
        for(var f:e.getValue().getClass().getDeclaredFields()){f.setAccessible(true);out.append(" "+f.getName()+"="+f.get(e.getValue()));}
        var top=v.getMethod("add",double.class,double.class,double.class).invoke(p,0,.65,0);
        var bottom=v.getMethod("add",double.class,double.class,double.class).invoke(p,0,-.75,0);
        out.append(" floor="+terrain.getMethod("gatherFloor",v,v).invoke(null,top,bottom)+"\n");
      }
      Files.writeString(root.resolve("build-cache/meme-breeding/inspect.txt"),out.toString());
    }catch(Exception x){try{Files.writeString(root.resolve("build-cache/meme-breeding/inspect.txt"),x.toString());}catch(Exception ignored){}}});
  }
}
