import dev.rehan.passthrough.HostGeometry;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.HashSet;
import java.util.List;

/** Offline cache-window simulation, not a Minecraft movement or frame-time benchmark. */
public final class HostGeometryCacheWalkCheck {
    private record Cell(int x,int y,int z) {}
    public static void main(String[] args)throws Exception{
        HostGeometry geometry=new HostGeometry(Path.of(args[0]));
        var rows=new ArrayList<String>();
        for(int budget:new int[]{8,16}){
            rows.add(run(geometry,"walk_straight",0.6,1.8,0.215,0,budget));
            rows.add(run(geometry,"walk_diagonal",0.6,1.8,0.152,0.152,budget));
            rows.add(run(geometry,"boat_straight",1.375,0.5625,0.4,0,budget));
            rows.add(run(geometry,"boat_diagonal",1.375,0.5625,0.283,0.283,budget));
        }
        String json="{\"scope\":\"offline synthetic body sweeps over actual clone triangles; not native movement\",\"results\":["+String.join(",",rows)+"]}";
        Files.writeString(Path.of(args[1]),json);System.out.println(json);
    }
    private static String run(HostGeometry geometry,String name,double width,double height,double dx,double dz,int coldLimit){
        var cache=new LinkedHashMap<Cell,List<HostGeometry.CollisionBox>>(32,0.75f,true);
        int cellsStored=0,holds=0,afterInitial=0,maxNeeded=0,maxReturned=0,failures=0,shapeOverruns=0,cellOverruns=0;long maxQuery=0;
        double x=8.5,y=93.02,z=-62.5;
        for(int step=0;step<60;step++){
            double[] low={x-width/2,y-0.08,z-width/2},high={x+width/2+dx,y+height,z+width/2+dz};
            var query=new ArrayList<Cell>();
            for(int bx=(int)Math.floor(low[0]-1e-5);bx<=(int)Math.floor(high[0]+1e-5);bx++)
            for(int by=(int)Math.floor(low[1]-1e-5);by<=(int)Math.floor(high[1]+1e-5);by++)
            for(int bz=(int)Math.floor(low[2]-1e-5);bz<=(int)Math.floor(high[2]+1e-5);bz++)query.add(new Cell(bx,by,bz));
            int needed=0;for(Cell cell:query)if(!cache.containsKey(cell))needed++;maxNeeded=Math.max(maxNeeded,needed);
            if(query.size()>64)cellOverruns++;
            for(int retry=0;retry<16;retry++){
                int cold=0,returned=0;boolean held=false;long start=System.nanoTime();
                var seen=new HashSet<HostGeometry.CollisionBox>();
                for(Cell cell:query){
                    var boxes=cache.get(cell);
                    if(boxes==null){
                        if(cold++>=coldLimit){held=true;break;}
                        try{boxes=geometry.movementBoxes(cell.x,cell.y,cell.z);}catch(HostGeometry.CollisionBudgetException e){failures++;boxes=List.of();}
                        cache.put(cell,boxes);cellsStored+=boxes.size();
                        while(cache.size()>192||cellsStored>32768){var first=cache.entrySet().iterator().next();cellsStored-=first.getValue().size();cache.remove(first.getKey());}
                    }
                    for(var b:boxes)if(b.maxX()>low[0]&&b.minX()<high[0]&&b.maxY()>low[1]&&b.minY()<high[1]&&b.maxZ()>low[2]&&b.minZ()<high[2]&&seen.add(b))returned++;
                }
                maxQuery=Math.max(maxQuery,System.nanoTime()-start);maxReturned=Math.max(maxReturned,returned);
                if(returned>4096)shapeOverruns++;
                if(!held)break;
                holds++;if(step>0)afterInitial++;
            }
            x+=dx;z+=dz;
        }
        if(coldLimit==16&&(afterInitial!=0||shapeOverruns!=0||cellOverruns!=0||failures!=0))throw new AssertionError("Common movement exceeded corrected budget: "+name);
        return String.format(java.util.Locale.ROOT,"{\"name\":\"%s\",\"coldLimit\":%d,\"steps\":60,\"warmupHolds\":%d,\"holdsAfterInitialStep\":%d,\"maxNewCellsInStep\":%d,\"maxReturnedShapes\":%d,\"denseCellFailures\":%d,\"shapeLimit4096Overruns\":%d,\"cellLimit64Overruns\":%d,\"maxQueryMs\":%.4f}",name,coldLimit,holds,afterInitial,maxNeeded,maxReturned,failures,shapeOverruns,cellOverruns,maxQuery/1e6);
    }
}
