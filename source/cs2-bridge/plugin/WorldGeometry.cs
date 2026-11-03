using System.Numerics;
namespace MinecraftBridge;

// Static map data is extracted locally from the user's VPK. No engine memory offsets.
sealed class WorldGeometry
{
    readonly record struct Triangle(Vector3 A,Vector3 B,Vector3 C) {
        public Vector3 Min=>Vector3.Min(A,Vector3.Min(B,C));
        public Vector3 Max=>Vector3.Max(A,Vector3.Max(B,C));
        public Vector3 Center=>(A+B+C)/3;
    }
    sealed class Node { public Vector3 Min,Max;public int Start,Count;public Node? Left,Right; }
    readonly Triangle[] triangles;
    readonly Node tree;
    public int Count=>triangles.Length;
    public WorldGeometry(string path) {
        using var r=new BinaryReader(File.OpenRead(path));if(r.ReadInt32()!=0x3152544d)throw new InvalidDataException("Map cache header");
        var count=r.ReadInt32();if(count<0||count>4000000)throw new InvalidDataException("Map cache count");
        triangles=new Triangle[count];Vector3 Read()=>new(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());
        for(int i=0;i<count;i++)triangles[i]=new(Read(),Read(),Read());
        tree=Build(0,count);
    }
    Node Build(int start,int count) {
        var node=new Node{Start=start,Count=count,Min=new(float.PositiveInfinity),Max=new(float.NegativeInfinity)};
        for(int i=start;i<start+count;i++){node.Min=Vector3.Min(node.Min,triangles[i].Min);node.Max=Vector3.Max(node.Max,triangles[i].Max);}
        if(count<=12)return node;
        var size=node.Max-node.Min;int axis=size.X>size.Y?(size.X>size.Z?0:2):(size.Y>size.Z?1:2);
        Array.Sort(triangles,start,count,Comparer<Triangle>.Create((a,b)=>a.Center[axis].CompareTo(b.Center[axis])));
        int half=count/2;node.Left=Build(start,half);node.Right=Build(start+half,count-half);node.Count=0;return node;
    }
    public bool Ray(Vector3 start,Vector3 end,out Vector3 hit) {
        float closest=1;var delta=end-start;bool found=Trace(tree,start,delta,ref closest);hit=start+delta*closest;return found;
    }
    static bool Box(Vector3 p,Vector3 d,Vector3 min,Vector3 max,float limit){
        float enter=0,exit=limit;
        for(int a=0;a<3;a++) {if(MathF.Abs(d[a])<1e-6f){if(p[a]<min[a]||p[a]>max[a])return false;continue;}
            float t0=(min[a]-p[a])/d[a],t1=(max[a]-p[a])/d[a];if(t0>t1)(t0,t1)=(t1,t0);enter=MathF.Max(enter,t0);exit=MathF.Min(exit,t1);if(enter>exit)return false;}
        return true;
    }
    bool Trace(Node node,Vector3 p,Vector3 d,ref float closest) {
        if(!Box(p,d,node.Min,node.Max,closest))return false;
        if(node.Left!=null)return Trace(node.Left,p,d,ref closest)|Trace(node.Right!,p,d,ref closest);
        bool found=false;for(int i=node.Start;i<node.Start+node.Count;i++){
            var t=triangles[i];var e1=t.B-t.A;var e2=t.C-t.A;var h=Vector3.Cross(d,e2);float det=Vector3.Dot(e1,h);if(MathF.Abs(det)<1e-7f)continue;
            var s=p-t.A;float u=Vector3.Dot(s,h)/det;if(u<0||u>1)continue;var q=Vector3.Cross(s,e1);float v=Vector3.Dot(d,q)/det;if(v<0||u+v>1)continue;
            float along=Vector3.Dot(e2,q)/det;if(along>0.0001f&&along<closest){closest=along;found=true;}
        }return found;
    }
    // Sample surfaces on a quarter-block grid. Conservative barrier voxels are streamed only near the player.
    public IEnumerable<(int x,int y,int z)> SurfaceCells() {
        var cells=new HashSet<(int,int,int)>();
        foreach(var t in triangles){
            var a=ToMC(t.A);var b=ToMC(t.B);var c=ToMC(t.C);var normal=Vector3.Normalize(Vector3.Cross(b-a,c-a));
            int n=Math.Clamp((int)MathF.Ceiling(MathF.Max(Vector3.Distance(a,b),MathF.Max(Vector3.Distance(a,c),Vector3.Distance(b,c)))*3),1,1500);
            for(int i=0;i<=n;i++)for(int j=0;j<=n-i;j++){
                var p=a+(b-a)*(i/(float)n)+(c-a)*(j/(float)n);
                // Floor surfaces belong to the block just below them. Vertical surfaces stay in their own voxel.
                if(MathF.Abs(normal.Y)>.5f)p.Y-=.035f;
                cells.Add(((int)MathF.Floor(p.X),(int)MathF.Floor(p.Y),(int)MathF.Floor(p.Z)));
            }
        }return cells;
    }
    public static Vector3 ToMC(Vector3 p)=>new(p.X/40,p.Z/40+96,-p.Y/40);
    public static Vector3 ToCS(Vector3 p)=>new(p.X*40,-p.Z*40,(p.Y-96)*40);
}
