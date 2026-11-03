using System.Numerics;
using System.Text.Json;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace MapPatchLab;

/// <summary>
/// Read-only inspection of one or more cut boxes against the map's native physics and
/// render resources. Reports, per box, which surface can be cut (physics mesh / render
/// mesh) and which cannot (convex hull colliders, per collision group). Writes nothing
/// except build-cache/map-patch-&lt;map&gt;/cut-diagnostics.json.
/// </summary>
public static class CutDiagnostics
{
    public sealed record BoxReport(
        int Box,
        string Min,
        string Max,
        int MeshTrianglesCentroidInside,
        int MeshTrianglesPositiveAreaInside,
        float? MeshCentroidZMin,
        float? MeshCentroidZMax,
        int HorizontalTrianglesInside,
        int MeshesTouched,
        int HullsInside,
        int HullsOverlappingNotInside,
        string[] HullGroups,
        string[] MeshGroups);

    public static object Run(Package package, string mapName, IReadOnlyList<CutBox> boxes, string output, bool hullsOnly = false, bool grow = false)
    {
        // Overlapping boxes are allowed here: this mode is read-only.
        if (boxes.Count == 0 || boxes.Any(box => !box.HasPositiveVolume)) throw new ArgumentException("every diagnostic box needs positive volume");
        const string physicsPath = "maps/{0}/world_physics.vphys_c";
        var path = string.Format(physicsPath, mapName);
        package.ReadEntry(package.FindEntry(path) ?? throw new FileNotFoundException(path), out byte[] original, false);
        using var resource = Parse(original);
        var physics = resource.DataBlock is PhysAggregateData direct ? direct : ((Model)resource.DataBlock).GetEmbeddedPhys() ?? throw new InvalidDataException("No embedded physics.");

        string Group(int index) => (string)physics.CollisionAttributes[index].Properties["m_CollisionGroupString"].Value!;
        var hullBounds = new List<(Vector3 Low, Vector3 High, int Group)>();
        foreach (var part in physics.Parts)
        {
            foreach (var descriptor in part.Shape.Hulls)
            {
                var bounds = (ValveResourceFormat.Serialization.KeyValues.KVObject)descriptor.Shape.Data.Properties["m_Bounds"].Value!;
                Vector3 V(string key) { var values = ((ValveResourceFormat.Serialization.KeyValues.KVObject)bounds.Properties[key].Value!).Properties.Values.Select(v => Convert.ToSingle(v.Value)).ToArray(); return new(values[0], values[1], values[2]); }
                hullBounds.Add((V("m_vMinBounds"), V("m_vMaxBounds"), descriptor.CollisionAttributeIndex));
            }
        }
        var grown = new List<CutBox>();
        foreach (var requested in boxes) grown.Add(grow ? GrowToContainHulls(requested, hullBounds, mapName) : requested);
        boxes = grown;
        var reports = new List<BoxReport>();
        for (var b = 0; b < boxes.Count; b++)
        {
            var box = boxes[b];
            var centroidInside = 0;
            var positiveInside = 0;
            var meshesTouched = 0;
            var horizontalInside = 0;
            float? centroidZMin = null, centroidZMax = null;
            var meshGroups = new SortedSet<string>(StringComparer.Ordinal);
            if (!hullsOnly)
            foreach (var part in physics.Parts)
            {
                foreach (var descriptor in part.Shape.Meshes)
                {
                    var vertices = descriptor.Shape.GetVertices().ToArray();
                    var touched = false;
                    foreach (var triangle in descriptor.Shape.GetTriangles())
                    {
                        var tri = new Triangle(vertices[triangle.X], vertices[triangle.Y], vertices[triangle.Z]);
                        var centre = (tri.A + tri.B + tri.C) / 3;
                        var normal = Vector3.Cross(tri.B - tri.A, tri.C - tri.A);
                        var horizontal = normal.Length() > 0 && Math.Abs(normal.Z) / normal.Length() > .7f;
                        if (centre.X >= box.Min.X && centre.X <= box.Max.X && centre.Y >= box.Min.Y && centre.Y <= box.Max.Y && centre.Z >= box.Min.Z && centre.Z <= box.Max.Z)
                        {
                            centroidInside++;
                            if (centroidZMin == null || centre.Z < centroidZMin) centroidZMin = centre.Z;
                            if (centroidZMax == null || centre.Z > centroidZMax) centroidZMax = centre.Z;
                            if (horizontal) horizontalInside++;
                        }
                        if (TriangleClipper.IntersectionArea(tri, box.Min, box.Max) > .001) { positiveInside++; touched = true; }
                    }
                    if (touched) { meshesTouched++; meshGroups.Add(Group(descriptor.CollisionAttributeIndex)); }
                }
            }

            var hullsInside = 0;
            var hullsOverlapping = 0;
            var hullGroups = new SortedSet<string>(StringComparer.Ordinal);
            var overlappingHullDetails = new List<string>();
            var partIndex = 0;
            foreach (var part in physics.Parts)
            {
                var hullIndex = 0;
                foreach (var descriptor in part.Shape.Hulls)
                {
                    var bounds = (ValveResourceFormat.Serialization.KeyValues.KVObject)descriptor.Shape.Data.Properties["m_Bounds"].Value!;
                    Vector3 V(string key) { var values = ((ValveResourceFormat.Serialization.KeyValues.KVObject)bounds.Properties[key].Value!).Properties.Values.Select(v => Convert.ToSingle(v.Value)).ToArray(); return new(values[0], values[1], values[2]); }
                    var low = V("m_vMinBounds"); var high = V("m_vMaxBounds");
                    if (!PhysicsMeshCutter.Overlaps(low, high, box.Min, box.Max)) { hullIndex++; continue; }
                    var fully = low.X >= box.Min.X && high.X <= box.Max.X && low.Y >= box.Min.Y && high.Y <= box.Max.Y && low.Z >= box.Min.Z && high.Z <= box.Max.Z;
                    if (fully) hullsInside++;
                    else
                    {
                        hullsOverlapping++;
                        var vertices = descriptor.Shape.GetVertexPositions().Length;
                        overlappingHullDetails.Add($"part {partIndex} hull {hullIndex} group={Group(descriptor.CollisionAttributeIndex)} vertices={vertices} bounds=({low.X:F0},{low.Y:F0},{low.Z:F0})..({high.X:F0},{high.Y:F0},{high.Z:F0})");
                    }
                    hullGroups.Add(Group(descriptor.CollisionAttributeIndex));
                    hullIndex++;
                }
                partIndex++;
            }

            reports.Add(new BoxReport(b + 1, box.Describe().Split(" max ")[0].Replace("min ", string.Empty), box.Describe().Split(" max ")[1],
                centroidInside, positiveInside, centroidZMin, centroidZMax, horizontalInside, meshesTouched, hullsInside, hullsOverlapping, hullGroups.ToArray(), meshGroups.ToArray()));
            Console.WriteLine($"box {b + 1} [{box.Describe()}]");
            Console.WriteLine($"  physics mesh: {centroidInside} triangle centroids inside ({horizontalInside} horizontal, z {centroidZMin:F0}..{centroidZMax:F0}), {positiveInside} with positive area inside, {meshesTouched} mesh descriptors touched, groups=[{string.Join(",", meshGroups)}]");
            Console.WriteLine($"  physics hull: {hullsInside} fully inside (removable), {hullsOverlapping} overlapping but NOT fully inside (not cuttable), groups=[{string.Join(",", hullGroups)}]");
            foreach (var detail in overlappingHullDetails.Take(40)) Console.WriteLine("    " + detail);
        }

        var report = new
        {
            status = "read-only-diagnostics",
            map = mapName,
            boxes = reports,
            note = "Mesh surfaces are cut exactly by PhysicsMeshCutter; convex hull colliders are never cut. A box with hulls inside cannot be completed by this pipeline."
        };
        Directory.CreateDirectory(output);
        var target = Path.Combine(output, "cut-diagnostics.json");
        File.WriteAllText(target, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        Console.WriteLine("diagnostics: " + target);
        return report;
    }

    static Resource Parse(byte[] bytes) { var resource = new Resource(); resource.Read(new MemoryStream(bytes), false); return resource; }

    /// <summary>
    /// Expands a box until every hull it overlaps lies entirely inside it. Hulls fully inside a
    /// box can be removed without deleting any collision outside the cut; hulls that straddle a
    /// box face cannot be cut by this pipeline.
    /// </summary>
    static CutBox GrowToContainHulls(CutBox box, List<(Vector3 Low, Vector3 High, int Group)> hulls, string mapName)
    {
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var min = box.Min; var max = box.Max;
            var expanded = 0;
            foreach (var hull in hulls)
            {
                if (!PhysicsMeshCutter.Overlaps(hull.Low, hull.High, box.Min, box.Max)) continue;
                var fully = hull.Low.X >= box.Min.X && hull.High.X <= box.Max.X && hull.Low.Y >= box.Min.Y && hull.High.Y <= box.Max.Y && hull.Low.Z >= box.Min.Z && hull.High.Z <= box.Max.Z;
                if (fully) continue;
                min = Vector3.Min(min, hull.Low); max = Vector3.Max(max, hull.High); expanded++;
            }
            if (expanded == 0) return box;
            box = new CutBox(min, max);
        }
        throw new InvalidDataException("hull containment did not converge for " + mapName);
    }
}
