import dev.rehan.passthrough.HostGeometry;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

/** Standalone source test; compile with HostGeometry.java only. Does not launch Minecraft. */
public final class HostGeometryMovementCheck {
    private static Path output;
    private static int assertions;
    private static void require(boolean value,String message){assertions++;if(!value)throw new AssertionError(message);}
    private static HostGeometry geometry(String name,List<double[]> triangles)throws Exception{
        ByteBuffer bytes=ByteBuffer.allocate(8+triangles.size()*36).order(ByteOrder.LITTLE_ENDIAN);
        bytes.putInt(0x3152544d).putInt(triangles.size());
        for(double[] t:triangles)for(int i=0;i<3;i++){
            bytes.putFloat((float)(t[i*3]*40));bytes.putFloat((float)(-t[i*3+2]*40));bytes.putFloat((float)((t[i*3+1]-96)*40));
        }
        Path path=output.resolve(name+".tri");Files.write(path,bytes.array());return new HostGeometry(path);
    }
    private static void rectangle(List<double[]> triangles,double x0,double x1,double z0,double z1,double y){
        triangles.add(new double[]{x0,y,z0,x1,y,z0,x1,y,z1});
        triangles.add(new double[]{x0,y,z0,x1,y,z1,x0,y,z1});
    }
    private static boolean covered(List<HostGeometry.CollisionBox> boxes,double x,double y,double z){
        for(var b:boxes)if(x>=b.minX()-1e-8&&x<=b.maxX()+1e-8&&y>=b.minY()-1e-6&&y<=b.maxY()+1e-6&&z>=b.minZ()-1e-8&&z<=b.maxZ()+1e-8)return true;
        return false;
    }
    public static void main(String[] args)throws Exception{
        output=Path.of(args[0]).toAbsolutePath();Files.createDirectories(output);
        var flat=new ArrayList<double[]>();rectangle(flat,-2,2,-2,2,93);
        HostGeometry plane=geometry("flat",flat);
        var floor=plane.movementBoxes(0,92,0);
        require(!floor.isEmpty(),"integer cell-boundary floor must survive clipping");
        for(int x=0;x<=20;x++)for(int z=0;z<=20;z++)require(covered(floor,x/20.0,93,z/20.0),"flat floor surface gap");
        require(floor.size()<=4,"flat rectangles should merge instead of staying hundreds of shapes");
        require(plane.movementBoxes(0,94,0).isEmpty(),"air above surface must stay air");
        var negative=plane.movementBoxes(-1,92,-1);
        require(covered(negative,-0.75,93,-0.75),"negative coordinates must use floor, not truncation");
        require(!covered(floor,0.5,92.8,0.5),"thin terrain surface must not fill the whole support cell");

        HostGeometry wall=geometry("wall",List.of(new double[]{0.37,91,-2,0.37,96,-2,0.37,96,2},new double[]{0.37,91,-2,0.37,96,2,0.37,91,2}));
        var wallBoxes=wall.movementBoxes(0,93,0);
        require(covered(wallBoxes,0.37,93.5,0.5),"wall missing");
        require(!covered(wallBoxes,0.8,93.5,0.5),"thin wall must not become an entire cell");

        var slope=geometry("slope",List.of(new double[]{0,93,0,1,93.2,0,1,93.3,1},new double[]{0,93,0,1,93.3,1,0,93.1,1}));
        var slopeBoxes=slope.movementBoxes(0,93,0);
        for(int x=1;x<40;x++)for(int z=1;z<40;z++)require(covered(slopeBoxes,x/40.0,93+0.2*x/40.0+0.1*z/40.0,z/40.0),"sloped surface gap");
        for(var b:slopeBoxes)require(b.maxY()-b.minY()<0.019,"gentle slope approximation grew beyond one projected cell");

        var ring=new ArrayList<double[]>();rectangle(ring,-1,0,-1,2,93);rectangle(ring,1,2,-1,2,93);rectangle(ring,0,1,-1,0,93);rectangle(ring,0,1,1,2,93);
        HostGeometry pit=geometry("pit",ring);
        require(pit.movementBoxes(0,92,0).isEmpty(),"TNT cut must not be refilled by a triangle AABB");
        require(!pit.movementBoxes(-1,92,0).isEmpty(),"rim of hole must retain support");
        var hit=plane.ray(new double[]{20,-20,0},new double[]{20,-20,-200});
        require(hit!=null&&Math.abs(hit.z()+120)<1e-6,"existing exact projectile ray changed");
        require(pit.ray(new double[]{20,-20,0},new double[]{20,-20,-200})==null,"exact arrow ray must pass through cut");

        var dense=new ArrayList<double[]>();for(int i=0;i<2049;i++)dense.add(flat.getFirst());
        boolean rejected=false;try{geometry("dense",dense).movementBoxes(0,92,0);}catch(HostGeometry.CollisionBudgetException e){rejected=true;}
        require(rejected,"dense triangle query must stop at a bounded budget");
        if(args.length>1){
            long start=System.nanoTime();HostGeometry actual=new HostGeometry(Path.of(args[1]));
            int samples=0,shapes=0;long slowest=0;
            for(int x=6;x<=10;x++)for(int y=90;y<=95;y++)for(int z=-64;z<=-60;z++){
                long before=System.nanoTime();shapes+=actual.movementBoxes(x,y,z).size();slowest=Math.max(slowest,System.nanoTime()-before);samples++;
            }
            System.out.printf("actual samples=%d shapes=%d maxCellMs=%.3f totalMs=%.3f%n",samples,shapes,slowest/1e6,(System.nanoTime()-start)/1e6);
        }
        System.out.println("HostGeometry movement checks passed: "+assertions);
    }
}
