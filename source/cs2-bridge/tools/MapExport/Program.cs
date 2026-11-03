using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using System.Numerics;
using var resource=new Resource();resource.Read(args[0]);
Console.WriteLine(resource.ResourceType);
var phys=(PhysAggregateData)resource.DataBlock;
Console.WriteLine($"Parts={phys.Parts.Length} Attributes={phys.CollisionAttributes.Count}");
for(int i=0;i<phys.CollisionAttributes.Count;i++)Console.WriteLine($"Attr {i}: {phys.CollisionAttributes[i].GetProperty<string>("m_CollisionGroupString")} tags={string.Join(',',phys.CollisionAttributes[i].GetArray<string>("m_InteractAsStrings")??Array.Empty<string>())}");
var triangles=new List<(Vector3 A,Vector3 B,Vector3 C)>();
bool Include(int attr)=>string.Equals(phys.CollisionAttributes[attr].GetProperty<string>("m_CollisionGroupString"),"Default",StringComparison.OrdinalIgnoreCase);
foreach(var part in phys.Parts){
 foreach(var mesh in part.Shape.Meshes){if(!Include(mesh.CollisionAttributeIndex))continue;var vertices=mesh.Shape.GetVertices();foreach(var tri in mesh.Shape.GetTriangles())triangles.Add((vertices[tri.X],vertices[tri.Y],vertices[tri.Z]));}
 foreach(var desc in part.Shape.Hulls){if(!Include(desc.CollisionAttributeIndex))continue;var hull=desc.Shape;var vertices=hull.GetVertexPositions();var edges=hull.GetEdges();foreach(var face in hull.GetFaces()){
  var poly=new List<int>();int start=face.Edge,e=start;do{poly.Add(edges[e].Origin);e=edges[e].Next;}while(e!=start && poly.Count<256);
  for(int i=1;i+1<poly.Count;i++)triangles.Add((vertices[poly[0]],vertices[poly[i]],vertices[poly[i+1]]));
 }}
}
using(var output=new BinaryWriter(File.Create(args[1]))){output.Write(0x3152544d);output.Write(triangles.Count);foreach(var t in triangles)foreach(var v in new[]{t.A,t.B,t.C}){output.Write(v.X);output.Write(v.Y);output.Write(v.Z);}}
Console.WriteLine($"Exported {triangles.Count} world collision triangles to local cache.");
