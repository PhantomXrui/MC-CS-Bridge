import dev.rehan.passthrough.HostGeometry;
import java.nio.file.*;
import java.util.*;
public class HostGeometryCheck {
    public static void main(String[] args) throws Exception {
        var g=new HostGeometry(Path.of(args[0]));int count=0,hits=0;double maxError=0;
        for(String line:Files.readAllLines(Path.of(args[1]))){
            double[] v=Arrays.stream(line.split("\t")).mapToDouble(Double::parseDouble).toArray();
            var h=g.ray(Arrays.copyOfRange(v,0,3),Arrays.copyOfRange(v,3,6));
            if((h!=null)!=(v[6]==1))throw new AssertionError("Hit mismatch at ray "+count);
            if(h!=null){hits++;double error=Math.sqrt(Math.pow(h.x()-v[7],2)+Math.pow(h.y()-v[8],2)+Math.pow(h.z()-v[9],2));
                maxError=Math.max(maxError,error);if(error>.03)throw new AssertionError("Position mismatch "+count+": "+error);}
            count++;
        }
        System.out.println("{\"pass\":true,\"rays\":"+count+",\"hits\":"+hits+",\"maxErrorCSUnits\":"+maxError+",\"triangles\":"+g.count()+"}");
    }
}
