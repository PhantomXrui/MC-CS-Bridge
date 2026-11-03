using System.Drawing;
using System.Globalization;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using V3=System.Numerics.Vector3;

namespace MinecraftBridge;
public sealed partial class MinecraftBridgePlugin {
    readonly Dictionary<(int x,int y,int z),string> blockMaterials=new();
    readonly Dictionary<(int,int,int),float[]> blockShapes=new();
    readonly Dictionary<uint,float> colliderScales=new();
    const int MaxBlockBodies=2048,MaxBodiesPerBlock=128;
    long lastBlockBudgetWarning;
    float cubeNativeSide;
    V3 cubeNativeMin;
    int BlockBodyCount=>blocks.Values.Sum(list=>list.Count);

    void RemoveBlockCollider((int,int,int) key){
        if(blocks.Remove(key,out var list))foreach(var body in list){colliderScales.Remove(body.Index);if(body.IsValid)body.Remove();}
        blockShapes.Remove(key);
    }
    void ClearBlockColliders(){
        foreach(var key in blocks.Keys.ToArray())RemoveBlockCollider(key);
        blockMaterials.Clear();colliderScales.Clear();blockShapes.Clear();
    }
    void ApplyBlockUpdates(JsonElement m){
        var shaped=new HashSet<(int,int,int)>();
        if(m.TryGetProperty("materials",out var materials))foreach(var a in materials.EnumerateArray()){
            if(a.GetArrayLength()!=4)continue;var key=(a[0].GetInt32(),a[1].GetInt32(),a[2].GetInt32());
            string material=a[3].GetString()??"minecraft:air";
            if(material is "minecraft:air" or "minecraft:barrier" or "minecraft:cave_air" or "minecraft:void_air")blockMaterials.Remove(key);
            else blockMaterials[key]=material;
        }
        if(m.TryGetProperty("shapes",out var shapes))foreach(var a in shapes.EnumerateArray()){
            if(a.GetArrayLength()<3||(a.GetArrayLength()-3)%6!=0)continue;
            var key=(a[0].GetInt32(),a[1].GetInt32(),a[2].GetInt32());shaped.Add(key);
            UpdateBlockShape(key,a.EnumerateArray().Skip(3).Select(v=>v.GetSingle()).ToArray());
        }
        if(m.TryGetProperty("clear",out var clear))for(int i=0;i+2<clear.GetArrayLength();i+=3){
            var key=(clear[i].GetInt32(),clear[i+1].GetInt32(),clear[i+2].GetInt32());
            if(!shaped.Contains(key)){RemoveBlockCollider(key);blockMaterials.Remove(key);}
        }
        if(m.TryGetProperty("set",out var set))for(int i=0;i+2<set.GetArrayLength();i+=3){
            var key=(set[i].GetInt32(),set[i+1].GetInt32(),set[i+2].GetInt32());
            if(!shaped.Contains(key))UpdateBlockShape(key,[0,0,0,1,1,1]);
        }
    }
    static List<(V3 at,float side)> TileCollisionBoxes((int x,int y,int z) key,float[] shape){
        var cubes=new List<(V3,float)>();
        for(int i=0;i+5<shape.Length;i+=6){
            var min=new V3(shape[i],shape[i+1],shape[i+2]);var max=new V3(shape[i+3],shape[i+4],shape[i+5]);var size=max-min;
            if(!float.IsFinite(min.X)||!float.IsFinite(min.Y)||!float.IsFinite(min.Z)||!float.IsFinite(max.X)||!float.IsFinite(max.Y)||!float.IsFinite(max.Z)||size.X<=0||size.Y<=0||size.Z<=0||size.X>4||size.Y>4||size.Z>4)continue;
            float side=Math.Min(size.X,Math.Min(size.Y,size.Z));if(side<.01f)continue;
            int nx=(int)Math.Ceiling(size.X/side),ny=(int)Math.Ceiling(size.Y/side),nz=(int)Math.Ceiling(size.Z/side);
            if((long)nx*ny*nz+cubes.Count>MaxBodiesPerBlock)throw new InvalidDataException("collision body budget per block");
            // Overlap the last cube on each axis: the union is exactly the requested box,
            // with no protruding full-block approximation for thin doors or piston heads.
            for(int x=0;x<nx;x++)for(int y=0;y<ny;y++)for(int z=0;z<nz;z++)
                cubes.Add((new V3(key.x,key.y,key.z)+min+new V3(Math.Min(x*side,size.X-side),Math.Min(y*side,size.Y-side),Math.Min(z*side,size.Z-side)),side));
        }
        return cubes;
    }
    void UpdateBlockShape((int x,int y,int z) key,float[] shape){
        if(shape.Length==0){RemoveBlockCollider(key);return;}
        if(blockShapes.TryGetValue(key,out var existing)&&existing.AsSpan().SequenceEqual(shape)&&blocks.TryGetValue(key,out var oldBodies)&&oldBodies.All(b=>b.IsValid))return;
        List<(V3 at,float side)> tiled;
        try{tiled=TileCollisionBoxes(key,shape);}catch(InvalidDataException){WarnBlockBudget();return;}
        blocks.TryGetValue(key,out var bodies);int before=bodies?.Count??0;
        if(BlockBodyCount-before+tiled.Count>MaxBlockBodies){WarnBlockBudget();return;}
        if(tiled.Count==0){RemoveBlockCollider(key);return;}
        if(before==0 && testing)Log($"New block {key} received at CS tick {Server.TickCount}");
        bodies??=new();
        while(bodies.Count>tiled.Count){var body=bodies[^1];colliderScales.Remove(body.Index);if(body.IsValid)body.Remove();bodies.RemoveAt(bodies.Count-1);}
        for(int i=0;i<tiled.Count;i++){
            var cube=tiled[i];
            CPhysicsPropOverride? body=i<bodies.Count?bodies[i]:null;
            if(body==null||!body.IsValid){
                body=Utilities.CreateEntityByName<CPhysicsPropOverride>("prop_physics_override");if(body==null)continue;
                // Source 2 must initialize the physics prop at its final world location.
                // Spawning/scaling at the origin before teleporting can leave a new platform
                // absent from player collision for the duration of a normal jump.
                if(body.CBodyComponent?.SceneNode?.Owner?.Entity is {} identity)identity.Flags&=~(uint)(1<<2);
                body.SetModel(CubeModel);
                var initial=new V3((cube.at.X+cube.side/2)*Units,-(cube.at.Z+cube.side/2)*Units,(cube.at.Y+cube.side/2-YOffset)*Units);
                body.Teleport(new Vector(initial.X,initial.Y,initial.Z),new QAngle(0,0,0),new Vector(0,0,0));
                body.DispatchSpawn();
                if(cubeNativeSide==0){cubeNativeSide=body.Collision.Maxs.X-body.Collision.Mins.X;cubeNativeMin=new V3(body.Collision.Mins.X,body.Collision.Mins.Y,body.Collision.Mins.Z);}
                if(cubeNativeSide<=0){body.Remove();continue;}
                body.Render=Color.FromArgb(0,255,255,255);
                if(Enum.TryParse<RenderMode_t>("kRenderNone",out var mode))body.RenderMode=mode;
                Utilities.SetStateChanged(body,"CBaseModelEntity","m_clrRender");
                if(i<bodies.Count)bodies[i]=body;else bodies.Add(body);
            }
            float scale=cube.side*Units/cubeNativeSide;
            var min=new V3(cube.at.X*Units,-(cube.at.Z+cube.side)*Units,(cube.at.Y-YOffset)*Units);
            var pos=min-cubeNativeMin*scale;
            body.Teleport(new Vector(pos.X,pos.Y,pos.Z),new QAngle(0,0,0),new Vector(0,0,0));
            if(!colliderScales.TryGetValue(body.Index,out float previous)||Math.Abs(previous-scale)>.00001f){body.AcceptInput("SetScale",body,body,scale.ToString(CultureInfo.InvariantCulture));colliderScales[body.Index]=scale;}
            // SetScale can rebuild the physics body. Freeze the resulting body,
            // including after piston shape changes, so map collision cannot fall.
            body.AcceptInput("DisableMotion");
            body.Teleport(new Vector(pos.X,pos.Y,pos.Z),new QAngle(0,0,0),new Vector(0,0,0));
        }
        blocks[key]=bodies;if(bodies.Count==tiled.Count)blockShapes[key]=shape;
    }
    void WarnBlockBudget(){if(Environment.TickCount64-lastBlockBudgetWarning>3000){lastBlockBudgetWarning=Environment.TickCount64;Log("MC collision budget reached; simplify the circuit or nearby blocks");}}
    void InspectBlockColliders(JsonElement m){
        var a=m.GetProperty("p");var key=(a[0].GetInt32(),a[1].GetInt32(),a[2].GetInt32());
        blocks.TryGetValue(key,out var list);
        WriteDiagnostic("block-colliders.json",JsonSerializer.Serialize(new{key,shape=blockShapes.GetValueOrDefault(key),cubeNativeSide,min=new[]{cubeNativeMin.X,cubeNativeMin.Y,cubeNativeMin.Z},bodies=list?.Where(e=>e.IsValid).Select(e=>new{id=e.Index,p=new[]{e.AbsOrigin!.X,e.AbsOrigin.Y,e.AbsOrigin.Z},min=new[]{e.Collision.Mins.X,e.Collision.Mins.Y,e.Collision.Mins.Z},max=new[]{e.Collision.Maxs.X,e.Collision.Maxs.Y,e.Collision.Maxs.Z},scale=colliderScales.GetValueOrDefault(e.Index)})}));
    }
}
