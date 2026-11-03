using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;

namespace MapPatchLab;

public static class NativeCutExperiment
{
    public static object Run(Package package, string output)
    {
        var min = new Vector3(280, 2440, -200); var max = new Vector3(360, 2520, -112);
        var artifacts = new List<object>();
        var variant = Path.Combine(output, "variant");
        foreach (var path in new[] { "maps/de_dust2/world_physics.vphys_c", "maps/de_dust2/world_physics.vmdl_c" })
        {
            package.ReadEntry(package.FindEntry(path) ?? throw new FileNotFoundException(path), out byte[] original, false);
            var container = ResourceContainer.Read(original);
            using var resource = Parse(original);
            var physics = Physics(resource);
            var blockType = path.EndsWith("vphys_c") ? "DATA" : "PHYS";
            var blockIndex = container.Blocks.FindIndex(b => b.Type == blockType);
            var block = container.Blocks[blockIndex].Bytes;
            if (BitConverter.ToUInt32(block) != Kv3V4Writer.Magic) throw new InvalidDataException("Expected binary KV3 v4 physics block.");
            var format = new Guid(block.AsSpan(4, 16));
            // First prove the serializer on this exact original tree before edits.
            container.Blocks[blockIndex] = new(blockType, Kv3V4Writer.Write(physics.Data, format));
            using (var noOp = Parse(container.Write())) Kv3V4Writer.AssertEquivalent(physics.Data, Physics(noOp).Data);
            var cut = PhysicsMeshCutter.CutMeshOnly(physics, min, max);
            container.Blocks[blockIndex] = new(blockType, Kv3V4Writer.Write(physics.Data, format));
            var bytes = container.Write();
            using var check = Parse(bytes);
            var checkedPhysics = Physics(check);
            Kv3V4Writer.AssertEquivalent(physics.Data, checkedPhysics.Data);
            foreach (var result in cut.Meshes)
            {
                var shape = checkedPhysics.Parts[0].Shape.Meshes[result.Index].Shape;
                var vertices = shape.GetVertices().ToArray();
                var triangles = shape.GetTriangles().ToArray().Select(t => new Triangle(vertices[t.X], vertices[t.Y], vertices[t.Z])).ToArray();
                PhysicsMeshCutter.ValidateBvh((byte[])shape.Data.Properties["m_Nodes"].Value!, triangles);
                if (triangles.Any(t => TriangleClipper.IntersectionArea(t, min, max) > .001)) throw new InvalidDataException("Serialized physics retains a positive-area triangle in the cut box.");
            }
            var target = Path.Combine(variant, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
            if (path.EndsWith(".vmdl_c", StringComparison.Ordinal)) ExportBridgeCache(checkedPhysics, bytes, output);
            artifacts.Add(new { resource = path, target, originalBytes = original.Length, patchedBytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)), roundtripTreeEquivalent = true, hullsUnchanged = checkedPhysics.Parts[0].Shape.Hulls.Length, cut });
        }

        const string renderPath = "maps/de_dust2/worldnodes/node000_world_lr0_agg37_22_hr_dust_blend_floortile_03.vmdl_c";
        package.ReadEntry(package.FindEntry(renderPath) ?? throw new FileNotFoundException(renderPath), out byte[] renderOriginal, false);
        var renderCut = ExactRenderCutter.Cut(renderOriginal, min, max);
        var renderBytes = renderCut.Bytes;
        var renderTarget = Path.Combine(variant, renderPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(renderTarget)!);
        File.WriteAllBytes(renderTarget, renderBytes);
        artifacts.Add(new { resource = renderPath, target = renderTarget, originalBytes = renderOriginal.Length, patchedBytes = renderBytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(renderBytes)), exactRenderCut = renderCut.Report });
        var report = new
        {
            status = "modified-native-resources-reparse-only", cutMin = new[] { min.X, min.Y, min.Z }, cutMax = new[] { max.X, max.Y, max.Z }, artifacts,
            limitations = new[] { "Native resource parser checks passed; game-engine acceptance is still untested.", "Physics cut is exact for intersecting meshes and refuses every overlapping hull.", "Render positions and UV channels are interpolated; new packed normal/tangent frames use a declared nearest-source shading approximation.", "No new pit side/bottom cap, MC fill, lighting, visibility or navmesh was generated.", "Keep original map resources; these local derived files must not be published." }
        };
        File.WriteAllText(Path.Combine(output, "native-cut-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        return report;
    }
    static Resource Parse(byte[] bytes) { var resource = new Resource(); resource.Read(new MemoryStream(bytes), false); return resource; }
    static PhysAggregateData Physics(Resource resource) => resource.DataBlock is PhysAggregateData physics ? physics : ((Model)resource.DataBlock).GetEmbeddedPhys() ?? throw new InvalidDataException("No embedded physics.");

    static void ExportBridgeCache(PhysAggregateData physics, byte[] sourceBytes, string output)
    {
        var cache = Path.Combine(Path.GetFullPath(Path.Combine(output, "..")), "map-physics", MapNamespaceBuilder.NewMapName);
        Directory.CreateDirectory(cache);
        var target = Path.Combine(cache, "world.tri");
        var triangles = new List<Triangle>();
        bool Include(int index) => string.Equals((string)physics.CollisionAttributes[index].Properties["m_CollisionGroupString"].Value!, "Default", StringComparison.OrdinalIgnoreCase);
        foreach (var part in physics.Parts)
        {
            foreach (var descriptor in part.Shape.Meshes)
            {
                if (!Include(descriptor.CollisionAttributeIndex)) continue;
                var vertices = descriptor.Shape.GetVertices().ToArray();
                foreach (var triangle in descriptor.Shape.GetTriangles()) triangles.Add(new(vertices[triangle.X], vertices[triangle.Y], vertices[triangle.Z]));
            }
            foreach (var descriptor in part.Shape.Hulls)
            {
                if (!Include(descriptor.CollisionAttributeIndex)) continue;
                var vertices = descriptor.Shape.GetVertexPositions().ToArray(); var edges = descriptor.Shape.GetEdges().ToArray();
                foreach (var face in descriptor.Shape.GetFaces())
                {
                    var polygon = new List<int>(); var first = (int)face.Edge; var edge = first;
                    do { if (edge < 0 || edge >= edges.Length || polygon.Count > 4096) throw new InvalidDataException("Invalid hull polygon."); polygon.Add(edges[edge].Origin); edge = edges[edge].Next; } while (edge != first);
                    for (var i = 1; i + 1 < polygon.Count; i++) triangles.Add(new(vertices[polygon[0]], vertices[polygon[i]], vertices[polygon[i + 1]]));
                }
            }
        }
        var min = new Vector3(280, 2440, -200); var max = new Vector3(360, 2520, -112);
        if (triangles.Any(t => TriangleClipper.IntersectionArea(t, min, max) > .001)) throw new InvalidDataException("Exported bridge cache puts geometry back inside the native cut.");
        using (var writer = new BinaryWriter(File.Create(target)))
        {
            writer.Write(0x3152544d); writer.Write(triangles.Count);
            foreach (var triangle in triangles) foreach (var v in new[] { triangle.A, triangle.B, triangle.C }) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
        }
        var report = new { status = "derived-from-patched-embedded-native-PHYS", path = target, triangles = triangles.Count, sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes)), cacheSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))), noPositiveAreaSurfaceInsideCut = true, collisionGroup = "Default", note = "Matches the bridge Default-group query policy; native map resource itself retains every original collision group." };
        File.WriteAllText(Path.Combine(cache, "cache-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
