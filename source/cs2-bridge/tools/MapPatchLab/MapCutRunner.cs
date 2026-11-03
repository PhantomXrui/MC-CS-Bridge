using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace MapPatchLab;

/// <summary>
/// Cuts an arbitrary map: native physics (vphys + embedded PHYS) and every render
/// aggregate model whose bounds intersect any cut box, then exports the bridge
/// collision cache. Several disjoint boxes are applied in one pass to the same
/// resources. Derived files only; the source VPK is never written.
/// </summary>
public static class MapCutRunner
{
    public static object Run(Package package, string mapName, IReadOnlyList<CutBox> boxes, string output, bool cutRender = true)
    {
        CutBox.Validate(boxes);
        var artifacts = new List<object>();
        var variant = Path.Combine(output, "variant");
        Directory.CreateDirectory(variant);

        foreach (var path in new[] { $"maps/{mapName}/world_physics.vphys_c" })
        {
            var entry = package.FindEntry(path);
            if (entry == null)
            {
                artifacts.Add(new { resource = path, skipped = "missing" });
                continue;
            }

            package.ReadEntry(entry, out byte[] original, false);
            var container = ResourceContainer.Read(original);
            using var resource = Parse(original);
            var physics = Physics(resource);
            var blockType = path.EndsWith("vphys_c", StringComparison.Ordinal) ? "DATA" : "PHYS";
            var blockIndex = container.Blocks.FindIndex(b => b.Type == blockType);
            if (blockIndex < 0) throw new InvalidDataException("Physics block missing: " + path);
            var block = container.Blocks[blockIndex].Bytes;
            if (BitConverter.ToUInt32(block) != Kv3V4Writer.Magic) throw new InvalidDataException($"Expected binary KV3 v4 physics block in {path}; format may have changed.");
            var format = new Guid(block.AsSpan(4, 16));
            container.Blocks[blockIndex] = new(blockType, Kv3V4Writer.Write(physics.Data, format));
            using (var noOp = Parse(container.Write())) Kv3V4Writer.AssertEquivalent(physics.Data, Physics(noOp).Data);
            var cut = PhysicsMeshCutter.CutMeshOnly(physics, boxes);
            container.Blocks[blockIndex] = new(blockType, Kv3V4Writer.Write(physics.Data, format));
            var bytes = container.Write();
            using var check = Parse(bytes);
            var checkedPhysics = Physics(check);
            Kv3V4Writer.AssertEquivalent(physics.Data, checkedPhysics.Data);
            var target = Path.Combine(variant, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
            if (path.EndsWith(".vmdl_c", StringComparison.Ordinal) || path.EndsWith(".vphys_c", StringComparison.Ordinal)) ExportBridgeCache(checkedPhysics, bytes, output, mapName, boxes);
            artifacts.Add(new { resource = path, target, originalBytes = original.Length, patchedBytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)), roundtripTreeEquivalent = true, hullsUnchanged = checkedPhysics.Parts[0].Shape.Hulls.Length, cut });
        }

        var prefix = $"maps/{mapName}/worldnodes/";
        var scanned = 0;
        var intersecting = 0;
        if (!cutRender)
        {
            Console.WriteLine("render cut skipped");
        }
        else
        {
            foreach (var list in package.Entries.Values)
            {
                foreach (var entry in list)
                {
                    var path = (string.IsNullOrEmpty(entry.DirectoryName) ? string.Empty : entry.DirectoryName + "/") + entry.FileName + "." + entry.TypeName;
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase)) continue;
                    scanned++;
                    package.ReadEntry(entry, out byte[] original, false);
                    if (!ModelIntersects(original, boxes)) continue;
                    intersecting++;
                    var cut = ExactRenderCutter.Cut(original, boxes);
                    var target = Path.Combine(variant, path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, cut.Bytes);
                    artifacts.Add(new { resource = path, target, originalBytes = original.Length, patchedBytes = cut.Bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(cut.Bytes)), exactRenderCut = cut.Report });
                }
            }
        }

        var report = new
        {
            status = "modified-native-resources-reparse-only",
            map = mapName,
            cutBoxes = boxes.Select((box, index) => new { box = index + 1, min = new[] { box.Min.X, box.Min.Y, box.Min.Z }, max = new[] { box.Max.X, box.Max.Y, box.Max.Z } }).ToArray(),
            renderModelsScanned = scanned,
            renderModelsCut = intersecting,
            artifacts,
            limitations = new[]
            {
                "Native resource parser checks passed; game-engine acceptance is still untested.",
                "Physics cut is exact for intersecting meshes and refuses every overlapping hull.",
                "No new pit side/bottom cap, MC fill, lighting, visibility or navmesh was generated.",
                "Keep original map resources; these local derived files must not be published."
            }
        };
        File.WriteAllText(Path.Combine(output, "native-cut-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        Console.WriteLine($"scanned={scanned} cut={intersecting}");
        return report;
    }

    static bool ModelIntersects(byte[] bytes, IReadOnlyList<CutBox> boxes)
    {
        using var resource = Parse(bytes);
        if (resource.DataBlock is not Model model) return false;
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        foreach (var (mesh, _, _) in model.GetEmbeddedMeshes())
        {
            mesh.GetBounds();
            low = Vector3.Min(low, mesh.MinBounds);
            high = Vector3.Max(high, mesh.MaxBounds);
        }
        if (low.X > high.X) return false;
        return boxes.Any(box => box.OverlapsBounds(low, high));
    }

    static Resource Parse(byte[] bytes) { var resource = new Resource(); resource.Read(new MemoryStream(bytes), false); return resource; }
    static PhysAggregateData Physics(Resource resource) => resource.DataBlock is PhysAggregateData physics ? physics : ((Model)resource.DataBlock).GetEmbeddedPhys() ?? throw new InvalidDataException("No embedded physics.");

    static void ExportBridgeCache(PhysAggregateData physics, byte[] sourceBytes, string output, string mapName, IReadOnlyList<CutBox> boxes)
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
        foreach (var box in boxes)
            if (triangles.Any(t => TriangleClipper.IntersectionArea(t, box.Min, box.Max) > .001))
                throw new InvalidDataException("Exported bridge cache puts geometry back inside the native cut.");
        using (var writer = new BinaryWriter(File.Create(target)))
        {
            writer.Write(0x3152544d); writer.Write(triangles.Count);
            foreach (var triangle in triangles) foreach (var v in new[] { triangle.A, triangle.B, triangle.C }) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
        }
        var report = new { status = "derived-from-patched-embedded-native-PHYS", path = target, triangles = triangles.Count, boxes = boxes.Count, noPositiveAreaSurfaceInsideAnyCut = true, sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes)), cacheSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))), collisionGroup = "Default", note = "Matches the bridge Default-group query policy; native map resource itself retains every original collision group." };
        File.WriteAllText(Path.Combine(cache, "cache-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
