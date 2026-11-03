using System.Numerics;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace MapPatchLab;

/// <summary>Read-only dump of one physics hull's serialized structure (development aid).</summary>
public static class HullProbe
{
    public static void Dump(PhysAggregateData physics, int partIndex, int hullIndex, int sampleCount)
    {
        var part = physics.Parts[partIndex];
        var descriptor = part.Shape.Hulls[hullIndex];
        var data = descriptor.Shape.Data;
        Console.WriteLine($"hull part={partIndex} index={hullIndex} collisionAttribute={descriptor.CollisionAttributeIndex}");
        foreach (var pair in data.Properties)
        {
            var kind = pair.Value.Value?.GetType().Name ?? "null";
            var size = pair.Value.Value is byte[] blob ? blob.Length.ToString() : pair.Value.Value is KVObject obj ? "kv:" + obj.Properties.Count : "";
            Console.WriteLine($"  key {pair.Key} type={pair.Value.Type} value={kind} {size}");
        }

        var vertices = descriptor.Shape.GetVertexPositions();
        Console.WriteLine($"  vertices={vertices.Length}");
        for (var i = 0; i < Math.Min(sampleCount, vertices.Length); i++) Console.WriteLine($"    v{i} = {vertices[i].X},{vertices[i].Y},{vertices[i].Z}");
        var edges = descriptor.Shape.GetEdges();
        Console.WriteLine($"  edges={edges.Length}");
        for (var i = 0; i < Math.Min(sampleCount, edges.Length); i++) Console.WriteLine($"    e{i} = origin={edges[i].Origin} next={edges[i].Next} twin={edges[i].Twin} face={edges[i].Face}");
        var faces = descriptor.Shape.GetFaces();
        Console.WriteLine($"  faces={faces.Length}");
        for (var i = 0; i < Math.Min(sampleCount, faces.Length); i++) Console.WriteLine($"    f{i} = edge={faces[i].Edge}");
        foreach (var pair in data.Properties)
        {
            if (pair.Value.Value is not KVObject array || !array.IsArray) continue;
            Console.WriteLine($"  array {pair.Key} count={array.Properties.Count}");
            foreach (var item in array.Properties.Take(2))
            {
                if (item.Value.Value is KVObject nested)
                    Console.WriteLine($"    [{item.Key}] keys: {string.Join(",", nested.Properties.Keys)}");
                else
                    Console.WriteLine($"    [{item.Key}] = {item.Value.Value}");
            }
        }
    }
}
