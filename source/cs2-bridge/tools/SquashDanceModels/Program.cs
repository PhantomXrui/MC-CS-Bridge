using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using MapPatchLab;

// Converts locally owned assets to local cache. Never edits the source VPK.
// This is a fixed-pose visual prop, not a replacement player pawn.
var root = args.Length > 0 ? Path.GetFullPath(args[0]) : @"D:\MC-CS-Bridge";
var modelPath = args.Length > 1 ? args[1] : "characters/models/tm_leet/tm_leet_varianta.vmdl_c";
var output = Path.Combine(root,"build-cache","dance-models");
var slug=Path.GetFileNameWithoutExtension(modelPath).Replace(".vmdl","");
const int steps=17;
using var package = new Package();
package.Read(Path.Combine(root,"games","Counter-Strike 2","csgo","pak01_dir.vpk"));
var entry = package.FindEntry(modelPath) ?? throw new FileNotFoundException(modelPath);
package.ReadEntry(entry, out byte[] original, false);
using var source=Read(original);
var allPoints=source.Blocks.OfType<VBIB>().SelectMany(b=>b.VertexBuffers).SelectMany(Points).ToArray();
var beforeMin=allPoints.Aggregate(new Vector3(float.PositiveInfinity),Vector3.Min);
var beforeMax=allPoints.Aggregate(new Vector3(float.NegativeInfinity),Vector3.Max);
var frames=new List<string>();
var reports=new List<object>();
for(int step=0;step<steps;step++){
    float squash=step/(float)(steps-1), horizontal=1+.55f*squash, vertical=1-.82f*squash;
    string framePath=$"models/mccs/dance/{slug}/s{step:00}.vmdl";
    using var resource=Read(original);
    var container=ResourceContainer.Read(original);
    var min=new Vector3(beforeMin.X*horizontal,beforeMin.Y*horizontal,0);
    var max=new Vector3(beforeMax.X*horizontal,beforeMax.Y*horizontal,(beforeMax.Z-beforeMin.Z)*vertical);
    for(int i=0;i<container.Blocks.Count;i++){
        var block=container.Blocks[i];
        if(block.Type=="MBUF"){
            var buffers=(VBIB)resource.Blocks[i];
            for(int b=0;b<buffers.VertexBuffers.Count;b++){
                var vb=buffers.VertexBuffers[b];
                vb.Data=vb.Data.ToArray();
                var pos=vb.InputLayoutFields.Single(f=>f.SemanticName=="POSITION");
                var joints=vb.InputLayoutFields.Single(f=>f.SemanticName=="BLENDINDICES");
                var weights=vb.InputLayoutFields.Single(f=>f.SemanticName=="BLENDWEIGHT");
                if(pos.Format.ToString()!="R32G32B32_FLOAT"||joints.Format.ToString()!="R8G8B8A8_UINT"||weights.Format.ToString()!="R8G8B8A8_UNORM")throw new NotSupportedException("Uninspected skinning layout.");
                for(int v=0;v<vb.ElementCount;v++){
                    var at=checked(v*(int)vb.ElementSizeInBytes);
                    var p=Position(vb,v);
                    Put(vb.Data,at+(int)pos.Offset,p.X*horizontal);
                    Put(vb.Data,at+(int)pos.Offset+4,p.Y*horizontal);
                    Put(vb.Data,at+(int)pos.Offset+8,(p.Z-beforeMin.Z)*vertical);
                    vb.Data.AsSpan(at+(int)joints.Offset,4).Clear();
                    vb.Data.AsSpan(at+(int)weights.Offset,4).Clear();
                    vb.Data[at+(int)weights.Offset]=255;
                }
                buffers.VertexBuffers[b]=vb;
            }
            container.Blocks[i]=new("MBUF",MbufWriter.Write(buffers,block.Bytes));
        }
        else if(block.Type is "MDAT" or "DATA" or "CTRL"){
            var kv=ParseKv(block.Bytes);
            if(block.Type=="MDAT")MakeStaticMesh(kv.Data,min,max);
            else if(block.Type=="DATA")MakeStaticModel(kv.Data,framePath,min,max);
            else {
                kv.Data.Properties.Remove("embedded_animation");
                kv.Data.Properties.Remove("embedded_physics");
                foreach(var item in Obj(kv.Data,"embedded_meshes").Properties.Values){
                    var mesh=(KVObject)item.Value;
                    mesh.Properties["morph_block"]=new KVValue(KVType.INT64,-1L);
                }
            }
            var bytes=Kv3V4Writer.Write(kv);
            Kv3V4Writer.AssertEquivalent(kv.Data,ParseKv(bytes).Data);
            container.Blocks[i]=new(block.Type,bytes);
        }
    }
    var generated=container.Write();
    using var check=Read(generated);
    var model=(Model)check.DataBlock;
    if(model.Skeleton.Bones.Length!=1)throw new InvalidDataException("Generated prop does not have exactly one stable root bone.");
    if(model.GetEmbeddedAnimations().Any())throw new InvalidDataException("Generated prop retained a live animation.");
    var checkPoints=check.Blocks.OfType<VBIB>().SelectMany(b=>b.VertexBuffers).SelectMany(Points).ToArray();
    if(checkPoints.Length!=allPoints.Length)throw new InvalidDataException("Vertex count changed.");
    for(int v=0;v<allPoints.Length;v++){
        var expected=new Vector3(allPoints[v].X*horizontal,allPoints[v].Y*horizontal,(allPoints[v].Z-beforeMin.Z)*vertical);
        if(Vector3.Distance(checkPoints[v],expected)>.0001f)throw new InvalidDataException("Geometry deformation did not roundtrip.");
    }
    foreach(var vb in check.Blocks.OfType<VBIB>().SelectMany(b=>b.VertexBuffers)){
        var joint=vb.InputLayoutFields.Single(f=>f.SemanticName=="BLENDINDICES");
        var weight=vb.InputLayoutFields.Single(f=>f.SemanticName=="BLENDWEIGHT");
        for(int v=0;v<vb.ElementCount;v++){
            int at=v*(int)vb.ElementSizeInBytes;
            if(!vb.Data.AsSpan(at+(int)joint.Offset,4).SequenceEqual(new byte[4])||!vb.Data.AsSpan(at+(int)weight.Offset,4).SequenceEqual(new byte[]{255,0,0,0}))throw new InvalidDataException("Unexpected skin weights.");
        }
    }
    string destination=Path.Combine(output,framePath.Replace('/',Path.DirectorySeparatorChar)+"_c");
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination,generated);
    frames.Add(framePath);
    reports.Add(new{step,squash,horizontal,vertical,min=new[]{min.X,min.Y,min.Z},max=new[]{max.X,max.Y,max.Z},bytes=generated.Length,sha256=Convert.ToHexString(SHA256.HashData(generated))});
}
var sourceName=modelPath.EndsWith("_c")?modelPath[..^2]:modelPath;
var manifestPath=Path.Combine(output,"manifest.json");
var models=new List<JsonElement>();
if(File.Exists(manifestPath)){
    using var existing=JsonDocument.Parse(File.ReadAllText(manifestPath));
    if(existing.RootElement.GetProperty("version").GetInt32()!=1||existing.RootElement.GetProperty("steps").GetInt32()!=steps)throw new InvalidDataException("Cannot merge a different manifest version or step count.");
    foreach(var model in existing.RootElement.GetProperty("models").EnumerateArray())
        if(model.GetProperty("source").GetString()!=sourceName)models.Add(model.Clone());
}
models.Add(JsonSerializer.SerializeToElement(new{source=sourceName,frames}));
var manifest=new {version=1,steps,models};
File.WriteAllText(manifestPath+".tmp",JsonSerializer.Serialize(manifest,new JsonSerializerOptions{WriteIndented=true}));
File.Move(manifestPath+".tmp",manifestPath,true);
File.WriteAllText(Path.Combine(output,slug+"-validation.json"),JsonSerializer.Serialize(new{source=modelPath,sourceHash=Convert.ToHexString(SHA256.HashData(original)),vertices=allPoints.Length,steps,pose="Original bind pose, single identity root bone, animation and physics disabled",groundAnchor=beforeMin.Z,shading="Original packed tangent frames retained; lighting approximation under nonuniform scale",engineAcceptance="PENDING",frames=reports},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Generated {steps} frames for {modelPath}, {allPoints.Length} vertices; max height {beforeMax.Z-beforeMin.Z:F3} -> {(beforeMax.Z-beforeMin.Z)*.18f:F3}. Engine acceptance pending. Manifest: {output}\\manifest.json");

static Resource Read(byte[] bytes){var r=new Resource();r.Read(new MemoryStream(bytes),false);return r;}
static BinaryKV3 ParseKv(byte[] bytes){var kv=new BinaryKV3{Offset=0,Size=(uint)bytes.Length};using var owner=new Resource();using var reader=new BinaryReader(new MemoryStream(bytes,false));kv.Read(reader,owner);return kv;}
static KVObject Obj(KVObject parent,string name)=>(KVObject)parent.Properties[name].Value;
static KVValue Number(float v)=>new(KVType.DOUBLE,(double)v);
static KVValue ArrayOf(params KVValue[] values){var arr=new KVObject("",true);foreach(var v in values)arr.AddProperty("",v);return new(KVType.ARRAY,arr);}
static KVValue Vec(params float[] values)=>ArrayOf(values.Select(Number).ToArray());
static void Empty(KVObject obj,params string[] names){foreach(var name in names)if(obj.Properties.ContainsKey(name))obj.Properties[name]=ArrayOf();}
static void Put(byte[] bytes,int at,float value)=>BitConverter.GetBytes(value).CopyTo(bytes,at);
static Vector3 Position(VBIB.OnDiskBufferData b,int i){var f=b.InputLayoutFields.Single(f=>f.SemanticName=="POSITION");int at=checked(i*(int)b.ElementSizeInBytes+(int)f.Offset);return new(BitConverter.ToSingle(b.Data,at),BitConverter.ToSingle(b.Data,at+4),BitConverter.ToSingle(b.Data,at+8));}
static IEnumerable<Vector3> Points(VBIB.OnDiskBufferData b){for(int i=0;i<b.ElementCount;i++)yield return Position(b,i);}
static void Bounds(KVObject obj,string lo,string hi,Vector3 min,Vector3 max){obj.Properties[lo]=Vec(min.X,min.Y,min.Z);obj.Properties[hi]=Vec(max.X,max.Y,max.Z);}
static void MakeStaticMesh(KVObject mesh,Vector3 min,Vector3 max){
    foreach(var item in Obj(mesh,"m_sceneObjects").Properties.Values){var scene=(KVObject)item.Value;Bounds(scene,"m_vMinBounds","m_vMaxBounds",min,max);}
    Empty(mesh,"m_constraints","m_attachments","m_hitboxsets");
    var skeleton=Obj(mesh,"m_skeleton");
    var bone=Obj(Obj(skeleton,"m_bones"),"0");
    bone.Properties["m_boneName"]=new(KVType.STRING,"mccs_dance_root");
    bone.Properties["m_parentName"]=new(KVType.STRING,"");
    bone.Properties["m_invBindPose"]=Vec(1,0,0,0,0,1,0,0,0,0,1,0);
    var center=(min+max)*.5f;var size=(max-min)*.5f;
    var bbox=Obj(bone,"m_bbox");bbox.Properties["m_vecCenter"]=Vec(center.X,center.Y,center.Z);bbox.Properties["m_vecSize"]=Vec(size.X,size.Y,size.Z);
    bone.Properties["m_flSphereRadius"]=Number(max.Length()+min.Length());
    skeleton.Properties["m_bones"]=ArrayOf(new KVValue(KVType.OBJECT,bone));
}
static void MakeStaticModel(KVObject model,string path,Vector3 min,Vector3 max){
    model.Properties["m_name"]=new(KVType.STRING,path);
    var info=Obj(model,"m_modelInfo");
    Bounds(info,"m_vHullMin","m_vHullMax",min,max);Bounds(info,"m_vViewMin","m_vViewMax",min,max);
    info.Properties["m_keyValueText"]=new(KVType.STRING,"");
    Empty(model,"m_refAnimGroups","m_refSequenceGroups","m_refAnimIncludeModels","m_refPhysicsData","m_refPhysicsHitboxData","m_boneFlexDrivers");
    var skel=Obj(model,"m_modelSkeleton");
    skel.Properties["m_boneName"]=ArrayOf(new KVValue(KVType.STRING,"mccs_dance_root"));
    skel.Properties["m_nParent"]=ArrayOf(new KVValue(KVType.INT64,-1L));
    skel.Properties["m_boneSphere"]=ArrayOf(Number(max.Length()+min.Length()));
    skel.Properties["m_nFlag"]=ArrayOf(Obj(skel,"m_nFlag").Properties["0"]);
    skel.Properties["m_bonePosParent"]=ArrayOf(Vec(0,0,0));
    skel.Properties["m_boneRotParent"]=ArrayOf(Vec(0,0,0,1));
    skel.Properties["m_boneScaleParent"]=ArrayOf(Number(1));
    var remap=Obj(model,"m_remappingTable");foreach(var key in remap.Properties.Keys.ToArray())remap.Properties[key]=new(KVType.INT64,0L);
}
