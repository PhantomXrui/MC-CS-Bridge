using MinecraftBridge;
using System.Numerics;
using System.Text.Json;
var geometry=new WorldGeometry(args.Contains("--projection-check")?@"D:\MC-CS-Bridge\build-cache\map-physics\de_dust2_mccs\world.tri":@"D:\MC-CS-Bridge\build-cache\map-physics\de_dust2\world.tri");
if(args.Contains("--projection-check")){
 using var state=JsonDocument.Parse(File.ReadAllText(@"D:\MC-CS-Bridge\logs\alignment-projection.json"));var s=state.RootElement[0];
 var p=s.GetProperty("hostCamera").GetString()!.Split(',').Select(x=>float.Parse(x,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
 var origin=WorldGeometry.ToCS(new Vector3(p[0],p[1],p[2]));
 float ya=(-s.GetProperty("hostYaw").GetSingle()-90)*MathF.PI/180,pi=s.GetProperty("hostPitch").GetSingle()*MathF.PI/180;
 var forward=new Vector3(MathF.Cos(ya)*MathF.Cos(pi),MathF.Sin(ya)*MathF.Cos(pi),-MathF.Sin(pi));var cameraRight=new Vector3(MathF.Sin(ya),-MathF.Cos(ya),0);var cameraUp=Vector3.Cross(cameraRight,forward);
 using var r=new BinaryReader(File.OpenRead(@"D:\MC-CS-Bridge\logs\cs2-depth.raw"));int width=r.ReadInt32(),height=r.ReadInt32(),format=r.ReadInt32();
 var data=new float[width*height];for(int i=0;i<data.Length;i++)data[i]=format==44?(r.ReadUInt32()&0xffffff)/16777215f:r.ReadSingle();
 using var meta=JsonDocument.Parse(File.ReadAllText(@"D:\MC-CS-Bridge\logs\cs2-depth-info.json"));
 var vp=meta.RootElement.GetProperty("viewport");float vx=vp[0].GetSingle(),vy=vp[1].GetSingle(),vw=vp[2].GetSingle(),vh=vp[3].GetSingle();
 var results=new List<object>();
 foreach(float fov in Enumerable.Range(55,36).Select(v=>(float)v).Append(73.7398f)){
  float tan=MathF.Tan(fov*MathF.PI/360);var errors=new List<float>();
  for(int y=height/8;y<height*7/8;y+=height/12)for(int x=width/8;x<width*7/8;x+=width/16){
   int dx=Math.Clamp((int)(vx+(x+.5f)/width*vw),0,width-1),dy=Math.Clamp((int)(vy+(y+.5f)/height*vh),0,height-1);
   float raw=data[dy*width+dx];if(raw<=0||raw>=.95f)continue;
   var ray=forward+cameraRight*((2*(x+.5f)/width-1)*tan*width/height)+cameraUp*((1-2*(y+.5f)/height)*tan);
   if(!geometry.Ray(origin,origin+ray*12000,out var at))continue;
   float z=Vector3.Dot(at-origin,forward)/40;if(z<.2f)continue;
   float expected=.95f*(409.6f-(.1625f*409.6f)/z)/(409.6f-.1625f);errors.Add(MathF.Abs(expected-raw));
  }
  errors.Sort();results.Add(new{fov,samples=errors.Count,median=errors.Count>0?errors[errors.Count/2]:1,within0005=errors.Count(e=>e<.0005f)});
 }
 Console.WriteLine(JsonSerializer.Serialize(new{width,height,format,results}));return;
}
if(args.Contains("--arrow-regression")){
 var cases=new[]{(name:"inside_coarse_ground",a:new Vector3(364,2460,112),b:new Vector3(460,2460,112)),
  (name:"real_floor",a:new Vector3(380,2460,160),b:new Vector3(380,2460,40)),
  (name:"mc_block_lane",a:new Vector3(420,2460,260),b:new Vector3(600,2460,260))};
 var cells=geometry.SurfaceCells().ToHashSet();
 foreach(var c in cases){bool terrainHit=geometry.Ray(c.a,c.b,out var at);var mc=WorldGeometry.ToMC(c.a);Console.WriteLine(JsonSerializer.Serialize(new{c.name,hit=terrainHit,at=new[]{at.X,at.Y,at.Z},mcHit=new[]{WorldGeometry.ToMC(at).X,WorldGeometry.ToMC(at).Y,WorldGeometry.ToMC(at).Z},insideBarrier=cells.Contains(((int)MathF.Floor(mc.X),(int)MathF.Floor(mc.Y),(int)MathF.Floor(mc.Z)))}));}return;
}
if(args.Contains("--arrow-fixtures")){
 var random=new Random(2505);var lines=new List<string>();
 for(int i=0;i<500;i++){
  var a=new Vector3(random.Next(-1800,1800),random.Next(-900,2600),random.Next(30,500));
  var b=a+new Vector3(random.Next(-800,800),random.Next(-800,800),random.Next(-400,400));
  bool found=geometry.Ray(a,b,out var at);
  lines.Add(string.Join('\t',new[]{a.X,a.Y,a.Z,b.X,b.Y,b.Z,found?1f:0f,at.X,at.Y,at.Z}.Select(n=>n.ToString("R",System.Globalization.CultureInfo.InvariantCulture))));
 }
 File.WriteAllLines(@"D:\MC-CS-Bridge\temp\arrow-geometry-fixtures.tsv",lines);Console.WriteLine("500 independent C# reference rays written");return;
}
if(args.Contains("--building-spots")){
 var spots=new[]{new Vector3(340,2460,128),new Vector3(1214,1105,128),new Vector3(-1280,320,512),new Vector3(-500,-800,180)};
 Console.WriteLine(JsonSerializer.Serialize(spots.Select(p=>geometry.Ray(p,p-new Vector3(0,0,600),out var hit)?new[]{hit.X,hit.Y,hit.Z+1}:null).Where(p=>p!=null)));
 return;
}
if(args.Contains("--combat-space")){
 var cells=geometry.SurfaceCells().ToHashSet();int found=0;
 for(int x=100;x<1700;x+=80)for(int y=1820;y<2700;y+=80){
  if(!geometry.Ray(new(x,y,100),new(x,y,-300),out var a)||!geometry.Ray(new(x+240,y,100),new(x+240,y,-300),out var b)||MathF.Abs(a.Z-b.Z)>2)continue;
  a.Z+=1;b.Z+=1;var combatEye=a+new Vector3(0,0,64);var target=b+new Vector3(0,0,64);
  if(geometry.Ray(combatEye,target,out _))continue;
  bool clear=true;for(int i=0;i<=30;i++)for(int h=64;h<=76;h+=12){var p=WorldGeometry.ToMC(Vector3.Lerp(a,b,i/30f)+new Vector3(0,0,h));if(cells.Contains(((int)MathF.Floor(p.X),(int)MathF.Floor(p.Y),(int)MathF.Floor(p.Z))))clear=false;}
  if(clear){Console.WriteLine($"player={a} bot={b} mcBot={WorldGeometry.ToMC(b)}");if(++found>=8)return;}
 }return;
}
if(args.Contains("--check-index")){
 var cells=geometry.SurfaceCells().ToArray();var index=new SurfaceIndex(cells);
 var centers=new List<float[]>{new[]{4.1f,93f,-59.2f},new[]{30.34f,95.98f,-27.63f},new[]{-31.99f,96f,7.99f},new[]{0f,96f,0f}};
 foreach(var c in centers){var expected=cells.Where(p=>Math.Abs(p.x-c[0])<=24&&Math.Abs(p.y-c[1])<=24&&Math.Abs(p.z-c[2])<=24).ToHashSet();var actual=index.Near(new[]{c}).ToHashSet();if(!expected.SetEquals(actual))throw new Exception("Spatial index changed collision coverage");Console.WriteLine($"Coverage matches at {string.Join(',',c)}: {actual.Count} cells");}return;
}
using var doc=JsonDocument.Parse(File.ReadAllText(@"D:\MC-CS-Bridge\logs\mc-frame-pose.json"));var cam=doc.RootElement.GetProperty("cam");var rot=doc.RootElement.GetProperty("rot");
var eye=WorldGeometry.ToCS(new Vector3(cam[0].GetSingle(),cam[1].GetSingle(),cam[2].GetSingle()));
float yaw=(-rot[0].GetSingle()-90)*MathF.PI/180,pitch=rot[1].GetSingle()*MathF.PI/180;
var dir=new Vector3(MathF.Cos(yaw)*MathF.Cos(pitch),MathF.Sin(yaw)*MathF.Cos(pitch),-MathF.Sin(pitch));
Console.WriteLine($"triangles={geometry.Count}; eye={eye}; direction={dir}");
Console.WriteLine(geometry.Ray(eye,eye+dir*4000,out var hit)?$"center wall={hit}, view depth blocks={Vector3.Distance(hit,eye)/40}":"miss");
foreach(var p in new[]{new Vector3(120,2300,0),new Vector3(320,2300,0)})Console.WriteLine(geometry.Ray(p,p-new Vector3(0,0,400),out hit)?$"ground {p} -> {hit}":"ground miss");
var samples=new List<object>();var right=new Vector3(MathF.Sin(yaw),-MathF.Cos(yaw),0);var up=Vector3.Cross(right,dir);float tangent=MathF.Tan(doc.RootElement.GetProperty("fov").GetSingle()*MathF.PI/360);
for(int y=80;y<640;y+=60)for(int x=80;x<1200;x+=80){var ray=dir+right*((2*(x+.5f)/1280-1)*tangent*1280/720)+up*((1-2*(y+.5f)/720)*tangent);if(geometry.Ray(eye,eye+ray*6000,out hit))samples.Add(new{x,y,z=Vector3.Dot(hit-eye,dir)/40});}
File.WriteAllText(@"D:\MC-CS-Bridge\logs\depth-geometry-samples.json",JsonSerializer.Serialize(samples));
