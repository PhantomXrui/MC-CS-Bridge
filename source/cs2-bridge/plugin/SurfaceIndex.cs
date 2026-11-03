namespace MinecraftBridge;

// Immutable spatial buckets: the game thread only examines nearby map cells.
sealed class SurfaceIndex {
    readonly Dictionary<(int,int,int),(int x,int y,int z)[]> buckets;
    public SurfaceIndex(IEnumerable<(int x,int y,int z)> cells) {
        buckets=cells.GroupBy(c=>(Tile(c.x),Tile(c.y),Tile(c.z))).ToDictionary(g=>g.Key,g=>g.ToArray());
    }
    static int Tile(int n)=>(int)Math.Floor(n/8.0);
    public IEnumerable<(int x,int y,int z)> Near(IReadOnlyList<float[]> centers) {
        // Inner buckets first so the floor directly around actors arrives first.
        for(int radius=0;radius<=3;radius++)foreach(var c in centers){
            int cx=Tile((int)Math.Floor(c[0])),cy=Tile((int)Math.Floor(c[1])),cz=Tile((int)Math.Floor(c[2]));
            for(int dx=-radius;dx<=radius;dx++)for(int dy=-radius;dy<=radius;dy++)for(int dz=-radius;dz<=radius;dz++){
                if(Math.Max(Math.Abs(dx),Math.Max(Math.Abs(dy),Math.Abs(dz)))!=radius)continue;
                if(!buckets.TryGetValue((cx+dx,cy+dy,cz+dz),out var cells))continue;
                foreach(var cell in cells)if(Math.Abs(cell.x-c[0])<=24&&Math.Abs(cell.y-c[1])<=24&&Math.Abs(cell.z-c[2])<=24)yield return cell;
            }
        }
    }
}
