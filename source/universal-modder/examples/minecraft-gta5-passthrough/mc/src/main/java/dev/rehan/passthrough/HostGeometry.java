package dev.rehan.passthrough;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashSet;
import java.util.List;

/** Read-only triangle BVH for the locally extracted CS collision cache, in CS units. */
public final class HostGeometry {
    public record Hit(double x, double y, double z, double nx, double ny, double nz) {}
    /** MC coordinates. The subdivision is conservative, not an exact triangle-shaped VoxelShape. */
    public record CollisionBox(double minX,double minY,double minZ,double maxX,double maxY,double maxZ) {
        double min(int axis){return axis==0?minX:axis==1?minY:minZ;}
        double max(int axis){return axis==0?maxX:axis==1?maxY:maxZ;}
    }
    public static final class CollisionBudgetException extends RuntimeException {
        public CollisionBudgetException(String message){super(message);}
    }
    private static final double MOVEMENT_GRID=1.0/16, SURFACE_SKIN=0.00001;
    // Must match the plugin's YOffset; start-map.ps1 passes the same value as -Dpassthrough.yOffset.
    private static final double Y_OFFSET=Double.parseDouble(System.getProperty("passthrough.yOffset","96"));
    private static final int MAX_CELL_TRIANGLES=2048, MAX_CELL_PIECES=4096;
    private final float[] vertices;
    private final int[] order;
    private final Node root;
    private static final class Node {
        final double[] min = {Double.POSITIVE_INFINITY,Double.POSITIVE_INFINITY,Double.POSITIVE_INFINITY};
        final double[] max = {Double.NEGATIVE_INFINITY,Double.NEGATIVE_INFINITY,Double.NEGATIVE_INFINITY};
        int start, count;
        Node left, right;
    }
    public HostGeometry(Path path) throws IOException {
        ByteBuffer data = ByteBuffer.wrap(Files.readAllBytes(path)).order(ByteOrder.LITTLE_ENDIAN);
        if (data.remaining()<8 || data.getInt()!=0x3152544d) throw new IOException("Invalid host geometry header");
        int count=data.getInt();
        if(count<1 || count>4_000_000 || data.remaining()!=(long)count*36) throw new IOException("Invalid host geometry length");
        vertices=new float[count*9]; order=new int[count];
        for(int i=0;i<vertices.length;i++) {vertices[i]=data.getFloat(); if(!Float.isFinite(vertices[i]))throw new IOException("Nonfinite host geometry");}
        for(int i=0;i<count;i++)order[i]=i;
        root=build(0,count);
    }
    public int count(){return order.length;}
    /** Only static triangles touching this one MC cell are considered; no world-sized voxel scan. */
    public List<CollisionBox> movementBoxes(int x,int y,int z){
        double[] low={x,y,z},high={x+1.0,y+1.0,z+1.0};
        double[] csMin={x*40.0,-(z+1.0)*40,(y-Y_OFFSET)*40};
        double[] csMax={(x+1.0)*40,-z*40.0,(y+1.0-Y_OFFSET)*40};
        var triangles=new ArrayList<Integer>();
        collect(root,csMin,csMax,triangles);
        var pieces=new ArrayList<CollisionBox>();
        for(int triangle:triangles){
            int o=triangle*9;
            List<double[]> polygon=new ArrayList<>(3);
            for(int i=0;i<3;i++)polygon.add(new double[]{vertices[o+i*3]/40.0,vertices[o+i*3+2]/40.0+96,-vertices[o+i*3+1]/40.0});
            for(int a=0;a<3&&!polygon.isEmpty();a++){
                polygon=clipPolygon(polygon,a,low[a],true);
                polygon=clipPolygon(polygon,a,high[a],false);
            }
            if(polygon.size()<3)continue;
            double[] a=polygon.get(0),b=polygon.get(1),c=null;
            double[] normal=null;
            for(int i=2;i<polygon.size();i++){
                c=polygon.get(i);
                normal=new double[]{(b[1]-a[1])*(c[2]-a[2])-(b[2]-a[2])*(c[1]-a[1]),
                    (b[2]-a[2])*(c[0]-a[0])-(b[0]-a[0])*(c[2]-a[2]),
                    (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0])};
                if(Math.abs(normal[0])+Math.abs(normal[1])+Math.abs(normal[2])>1e-14)break;
            }
            if(normal==null||Math.abs(normal[0])+Math.abs(normal[1])+Math.abs(normal[2])<=1e-14)continue;
            int dominant=0;for(int i=1;i<3;i++)if(Math.abs(normal[i])>Math.abs(normal[dominant]))dominant=i;
            int u=(dominant+1)%3,v=(dominant+2)%3;
            double[] min=bounds(polygon,false),max=bounds(polygon,true);
            // A clipped axis-aligned rectangle is already exact and need not become 256 little boxes.
            if(max[dominant]-min[dominant]<1e-10 && Math.abs(projectedArea(polygon,u,v)-(max[u]-min[u])*(max[v]-min[v]))<1e-10){
                addPiece(pieces,min,max);continue;
            }
            int startU=(int)Math.floor(min[u]/MOVEMENT_GRID),endU=(int)Math.ceil(max[u]/MOVEMENT_GRID)-1;
            int startV=(int)Math.floor(min[v]/MOVEMENT_GRID),endV=(int)Math.ceil(max[v]/MOVEMENT_GRID)-1;
            for(int iu=startU;iu<=endU;iu++)for(int iv=startV;iv<=endV;iv++){
                var part=clipPolygon(polygon,u,iu*MOVEMENT_GRID,true);
                part=clipPolygon(part,u,(iu+1)*MOVEMENT_GRID,false);
                part=clipPolygon(part,v,iv*MOVEMENT_GRID,true);
                part=clipPolygon(part,v,(iv+1)*MOVEMENT_GRID,false);
                if(part.size()<3||projectedArea(part,u,v)<1e-14)continue;
                addPiece(pieces,bounds(part,false),bounds(part,true));
            }
        }
        // Merge touching boxes without filling gaps or rounding heights. Flat floors/walls become a few shapes.
        List<CollisionBox> merged=new ArrayList<>(new HashSet<>(pieces));
        for(int axis=0;axis<3;axis++)merged=mergeBoxes(merged,axis);
        return List.copyOf(merged);
    }
    private void collect(Node node,double[] low,double[] high,List<Integer> result){
        for(int a=0;a<3;a++)if(node.max[a]<low[a]-0.001||node.min[a]>high[a]+0.001)return;
        if(node.left!=null){collect(node.left,low,high,result);collect(node.right,low,high,result);return;}
        for(int i=node.start;i<node.start+node.count;i++){
            int t=order[i],o=t*9;boolean overlaps=true;
            for(int a=0;a<3;a++){
                double lo=Math.min(vertices[o+a],Math.min(vertices[o+3+a],vertices[o+6+a]));
                double hi=Math.max(vertices[o+a],Math.max(vertices[o+3+a],vertices[o+6+a]));
                if(hi<low[a]-0.001||lo>high[a]+0.001){overlaps=false;break;}
            }
            if(overlaps){if(result.size()>=MAX_CELL_TRIANGLES)throw new CollisionBudgetException("terrain cell triangle budget");result.add(t);}
        }
    }
    private static List<double[]> clipPolygon(List<double[]> input,int axis,double plane,boolean keepAbove){
        var output=new ArrayList<double[]>(input.size()+1);if(input.isEmpty())return output;
        double[] previous=input.getLast();boolean previousInside=keepAbove?previous[axis]>=plane:previous[axis]<=plane;
        for(double[] current:input){
            boolean inside=keepAbove?current[axis]>=plane:current[axis]<=plane;
            if(inside!=previousInside){
                double t=(plane-previous[axis])/(current[axis]-previous[axis]);
                double[] p=new double[3];for(int a=0;a<3;a++)p[a]=previous[a]+t*(current[a]-previous[a]);
                p[axis]=plane;appendPoint(output,p);
            }
            if(inside)appendPoint(output,current);previous=current;previousInside=inside;
        }
        if(output.size()>1&&samePoint(output.getFirst(),output.getLast()))output.removeLast();
        return output;
    }
    private static void appendPoint(List<double[]> output,double[] p){if(output.isEmpty()||!samePoint(output.getLast(),p))output.add(p);}
    private static boolean samePoint(double[] a,double[] b){return Math.abs(a[0]-b[0])+Math.abs(a[1]-b[1])+Math.abs(a[2]-b[2])<1e-12;}
    private static double[] bounds(List<double[]> polygon,boolean maximum){
        double init=maximum?Double.NEGATIVE_INFINITY:Double.POSITIVE_INFINITY;
        double[] value={init,init,init};
        for(double[] p:polygon)for(int a=0;a<3;a++)value[a]=maximum?Math.max(value[a],p[a]):Math.min(value[a],p[a]);
        return value;
    }
    private static double projectedArea(List<double[]> polygon,int u,int v){
        if(polygon.size()<3)return 0;
        double area=0;double[] origin=polygon.getFirst(),previous=polygon.getLast();
        for(double[] p:polygon){area+=(previous[u]-origin[u])*(p[v]-origin[v])-(p[u]-origin[u])*(previous[v]-origin[v]);previous=p;}
        return Math.abs(area)*0.5;
    }
    private static void addPiece(List<CollisionBox> result,double[] low,double[] high){
        if(result.size()>=MAX_CELL_PIECES)throw new CollisionBudgetException("terrain cell surface budget");
        // Vanilla accepts the FIRST step height that improves horizontal progress.
        // Hundreds of distinct submillimetre floor heights therefore stop each tick at
        // the next tiny ledge. Quantize only shallow horizontal surfaces to our existing
        // 1/16 grid; adjacent floor pieces now share a level and merge. Walls stay exact.
        if(high[1]-low[1] <= Math.max(high[0]-low[0],high[2]-low[2])*.5){
            low[1]=Math.floor((low[1]+1e-7)/MOVEMENT_GRID)*MOVEMENT_GRID;
            high[1]=Math.ceil((high[1]-1e-7)/MOVEMENT_GRID)*MOVEMENT_GRID;
        }
        for(int a=0;a<3;a++)if(high[a]-low[a]<SURFACE_SKIN){double middle=(high[a]+low[a])/2;low[a]=middle-SURFACE_SKIN/2;high[a]=middle+SURFACE_SKIN/2;}
        result.add(new CollisionBox(low[0],low[1],low[2],high[0],high[1],high[2]));
    }
    private static List<CollisionBox> mergeBoxes(List<CollisionBox> input,int axis){
        int u=(axis+1)%3,v=(axis+2)%3;
        input.sort(Comparator.comparingDouble((CollisionBox b)->b.min(u)).thenComparingDouble(b->b.max(u))
            .thenComparingDouble(b->b.min(v)).thenComparingDouble(b->b.max(v)).thenComparingDouble(b->b.min(axis)));
        var output=new ArrayList<CollisionBox>();CollisionBox last=null;
        for(var box:input){
            if(last!=null&&last.min(u)==box.min(u)&&last.max(u)==box.max(u)&&last.min(v)==box.min(v)&&last.max(v)==box.max(v)
                    &&box.min(axis)<=last.max(axis)+1e-12){
                double[] low={last.minX,last.minY,last.minZ},high={last.maxX,last.maxY,last.maxZ};high[axis]=Math.max(high[axis],box.max(axis));
                last=new CollisionBox(low[0],low[1],low[2],high[0],high[1],high[2]);output.set(output.size()-1,last);
            }else{last=box;output.add(box);}
        }
        return output;
    }
    private double center(int triangle,int axis){int o=triangle*9+axis;return (vertices[o]+vertices[o+3]+vertices[o+6])/3.0;}
    private Node build(int start,int count){
        Node n=new Node();n.start=start;n.count=count;
        for(int i=start;i<start+count;i++)for(int v=0;v<3;v++)for(int a=0;a<3;a++){
            double p=vertices[order[i]*9+v*3+a];n.min[a]=Math.min(n.min[a],p);n.max[a]=Math.max(n.max[a],p);
        }
        if(count<=12)return n;
        int axis=0;for(int a=1;a<3;a++)if(n.max[a]-n.min[a]>n.max[axis]-n.min[axis])axis=a;
        int middle=start+count/2,lo=start,hi=start+count-1;
        while(lo<hi){
            double pivot=center(order[(lo+hi)>>>1],axis);int i=lo,j=hi;
            while(i<=j){while(center(order[i],axis)<pivot)i++;while(center(order[j],axis)>pivot)j--;
                if(i<=j){int t=order[i];order[i++]=order[j];order[j--]=t;}}
            if(middle<=j)hi=j;else if(middle>=i)lo=i;else break;
        }
        n.left=build(start,middle-start);n.right=build(middle,start+count-middle);n.count=0;return n;
    }
    private static final class Ray {
        final double[] p,d;double nearest=1;int triangle=-1;
        Ray(double[] p,double[] end){this.p=p;d=new double[]{end[0]-p[0],end[1]-p[1],end[2]-p[2]};}
    }
    public Hit ray(double[] start,double[] end){
        Ray r=new Ray(start,end);trace(root,r);if(r.triangle<0)return null;
        int o=r.triangle*9;double ax=vertices[o+3]-vertices[o],ay=vertices[o+4]-vertices[o+1],az=vertices[o+5]-vertices[o+2];
        double bx=vertices[o+6]-vertices[o],by=vertices[o+7]-vertices[o+1],bz=vertices[o+8]-vertices[o+2];
        double nx=ay*bz-az*by,ny=az*bx-ax*bz,nz=ax*by-ay*bx,len=Math.sqrt(nx*nx+ny*ny+nz*nz);
        if(nx*r.d[0]+ny*r.d[1]+nz*r.d[2]>0)len=-len;
        return new Hit(start[0]+r.d[0]*r.nearest,start[1]+r.d[1]*r.nearest,start[2]+r.d[2]*r.nearest,nx/len,ny/len,nz/len);
    }
    private void trace(Node n,Ray r){
        double enter=0,exit=r.nearest;
        for(int a=0;a<3;a++){
            if(Math.abs(r.d[a])<1e-9){if(r.p[a]<n.min[a]||r.p[a]>n.max[a])return;continue;}
            double t0=(n.min[a]-r.p[a])/r.d[a],t1=(n.max[a]-r.p[a])/r.d[a];
            enter=Math.max(enter,Math.min(t0,t1));exit=Math.min(exit,Math.max(t0,t1));if(enter>exit)return;
        }
        if(n.left!=null){trace(n.left,r);trace(n.right,r);return;}
        for(int i=n.start;i<n.start+n.count;i++){
            int t=order[i],o=t*9;
            double ax=vertices[o+3]-vertices[o],ay=vertices[o+4]-vertices[o+1],az=vertices[o+5]-vertices[o+2];
            double bx=vertices[o+6]-vertices[o],by=vertices[o+7]-vertices[o+1],bz=vertices[o+8]-vertices[o+2];
            double hx=r.d[1]*bz-r.d[2]*by,hy=r.d[2]*bx-r.d[0]*bz,hz=r.d[0]*by-r.d[1]*bx;
            double det=ax*hx+ay*hy+az*hz;if(Math.abs(det)<1e-9)continue;
            double sx=r.p[0]-vertices[o],sy=r.p[1]-vertices[o+1],sz=r.p[2]-vertices[o+2];
            double u=(sx*hx+sy*hy+sz*hz)/det;if(u<0||u>1)continue;
            double qx=sy*az-sz*ay,qy=sz*ax-sx*az,qz=sx*ay-sy*ax;
            double v=(r.d[0]*qx+r.d[1]*qy+r.d[2]*qz)/det;if(v<0||u+v>1)continue;
            double distance=(bx*qx+by*qy+bz*qz)/det;
            if(distance>0.000001&&distance<r.nearest){r.nearest=distance;r.triangle=t;}
        }
    }
}
