using System.Numerics;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace MapPatchLab;

public static class PhysicsMeshCutter
{
    const float Padding = 1f / 32;
    // Box is the zero-based index into Result.Boxes (the report's cutBoxes array is one-based).
    public sealed record BoxEffect(int Box, int AffectedTriangles, double AreaRemoved);
    public sealed record MeshResult(int Index, int OriginalTriangles, int OutputTriangles, int AffectedTriangles, int Nodes, double AreaRemoved, BoxEffect[] Boxes);
    public sealed record Result(CutBox[] Boxes, int OriginalBvhChecks, MeshResult[] Meshes);
    readonly record struct Item(Triangle Triangle, int Material);
    readonly record struct Node(Vector3 Min, Vector3 Max, uint Packed, uint TriangleOffset);

    public static Result CutMeshOnly(PhysAggregateData physics, Vector3 min, Vector3 max) =>
        CutMeshOnly(physics, new[] { new CutBox(min, max) });

    // This deliberately refuses the convex-hull case. Deleting a crossing solid
    // hull wholesale would remove collision outside the requested cut volume.
    // Several disjoint boxes are subtracted from the same tree in the order given.
    public static Result CutMeshOnly(PhysAggregateData physics, IReadOnlyList<CutBox> boxes)
    {
        CutBox.Validate(boxes);
        var results = new List<MeshResult>();
        var checks = 0;
        var meshIndex = 0;
        var overlappingHulls = 0;
        foreach (var candidate in physics.Parts)
        {
            foreach (var descriptor in candidate.Shape.Hulls)
            {
                var hullBounds = Sub(descriptor.Shape.Data, "m_Bounds");
                if (boxes.Any(box => Overlaps(Vector(hullBounds, "m_vMinBounds"), Vector(hullBounds, "m_vMaxBounds"), box.Min, box.Max))) overlappingHulls++;
            }
        }
        foreach (var part in physics.Parts)
        {
        for (var index = 0; index < part.Shape.Meshes.Length; index++, meshIndex++)
        {
            var mesh = part.Shape.Meshes[index].Shape;
            var data = mesh.Data;
            var sourceVertices = mesh.GetVertices().ToArray();
            var sourceTriangles = mesh.GetTriangles().ToArray();
            var materialValues = Array(data, "m_Materials").Properties.Values.Select(v => Convert.ToInt32(v.Value)).ToArray();
            if (materialValues.Length != 0 && materialValues.Length != sourceTriangles.Length) throw new InvalidDataException("Unexpected per-triangle material array.");
            var input = sourceTriangles.Select(t => new Triangle(sourceVertices[t.X], sourceVertices[t.Y], sourceVertices[t.Z])).ToArray();
            ValidateBvh((byte[])data.Properties["m_Nodes"].Value!, input);
            checks++;
            var output = new List<Item>();
            var affectedPerBox = new int[boxes.Count];
            var removedPerBox = new double[boxes.Count];
            var affected = 0;
            double removed = 0;
            for (var i = 0; i < input.Length; i++)
            {
                var tri = input[i];
                var area = TriangleClipper.Area(tri);
                var pieces = new List<Triangle> { tri };
                for (var b = 0; b < boxes.Count && pieces.Count > 0; b++)
                {
                    var box = boxes[b];
                    var next = new List<Triangle>(pieces.Count);
                    foreach (var piece in pieces)
                    {
                        var pieceMin = Vector3.Min(piece.A, Vector3.Min(piece.B, piece.C));
                        var pieceMax = Vector3.Max(piece.A, Vector3.Max(piece.B, piece.C));
                        if (!Overlaps(pieceMin, pieceMax, box.Min, box.Max)) { next.Add(piece); continue; }
                        var before = TriangleClipper.Area(piece);
                        var parts = TriangleClipper.SubtractBox(piece, box.Min, box.Max);
                        var after = parts.Sum(TriangleClipper.Area);
                        if (before - after > Math.Max(0.001, before * 1e-6)) { affectedPerBox[b]++; removedPerBox[b] += before - after; }
                        next.AddRange(parts);
                    }
                    pieces = next;
                }
                var remainder = pieces.Sum(TriangleClipper.Area);
                if (area - remainder > Math.Max(0.001, area * 1e-6)) { affected++; removed += area - remainder; }
                foreach (var piece in pieces) if (TriangleClipper.Area(piece) > 5e-11) output.Add(new(piece, materialValues.Length == 0 ? 0 : materialValues[i]));
            }
            if (affected == 0) continue;
            if (output.Count == 0) throw new InvalidDataException("Removing a complete mesh descriptor is not implemented.");
            var nodes = new List<Node>();
            var ordered = new List<Item>();
            Build(output, nodes, ordered);
            var vertices = new List<Vector3>();
            var vertexIds = new Dictionary<Vector3, int>();
            using var triStream = new MemoryStream();
            using var triWriter = new BinaryWriter(triStream);
            foreach (var item in ordered)
            {
                foreach (var vertex in new[] { item.Triangle.A, item.Triangle.B, item.Triangle.C })
                {
                    if (!vertexIds.TryGetValue(vertex, out var id)) { id = vertices.Count; vertices.Add(vertex); vertexIds.Add(vertex, id); }
                    triWriter.Write(id);
                }
                foreach (var box in boxes)
                {
                    var again = TriangleClipper.SubtractBox(item.Triangle, box.Min, box.Max).Sum(TriangleClipper.Area);
                    if (Math.Abs(again - TriangleClipper.Area(item.Triangle)) > Math.Max(0.01, TriangleClipper.Area(item.Triangle) * 1e-4)) throw new InvalidDataException("Cut physics still contains positive-area surface inside a requested box.");
                }
            }
            using var vertexStream = new MemoryStream();
            using var vertexWriter = new BinaryWriter(vertexStream);
            foreach (var vertex in vertices) WriteVector(vertexWriter, vertex);
            using var nodeStream = new MemoryStream();
            using var nodeWriter = new BinaryWriter(nodeStream);
            foreach (var node in nodes) { WriteVector(nodeWriter, node.Min); nodeWriter.Write(node.Packed); WriteVector(nodeWriter, node.Max); nodeWriter.Write(node.TriangleOffset); }
            var nodeBytes = nodeStream.ToArray();
            ValidateBvh(nodeBytes, ordered.Select(t => t.Triangle).ToArray());
            data.Properties["m_Nodes"] = new(KVType.BINARY_BLOB, nodeBytes);
            data.Properties["m_Triangles"] = new(KVType.BINARY_BLOB, triStream.ToArray());
            data.Properties["m_Vertices"] = new(KVType.BINARY_BLOB, vertexStream.ToArray());
            if (materialValues.Length != 0) data.Properties["m_Materials"] = NumberArray(ordered.Select(i => (object)(long)i.Material), KVType.INT64);
            data.Properties["m_vMin"] = VectorValue(nodes[0].Min);
            data.Properties["m_vMax"] = VectorValue(nodes[0].Max);
            // Preserve m_vOrthographicAreas: actual values are normalized 0..1,
            // not the triangle projection areas their name might suggest. Its
            // compiler-specific sampling formula is unverified. This static-world
            // experiment changes only vertices/triangles/material order/BVH/bounds.
            var effects = boxes.Select((_, b) => new BoxEffect(b, affectedPerBox[b], removedPerBox[b])).Where(effect => effect.AffectedTriangles != 0).ToArray();
            results.Add(new(meshIndex, input.Length, ordered.Count, affected, nodes.Count, removed, effects));
        }
        }
        if (results.Count == 0) throw new InvalidDataException("The candidate box removes no mesh surface.");
        return new(boxes.ToArray(), checks, results.ToArray());
    }

    static void Build(List<Item> items, List<Node> nodes, List<Item> ordered)
    {
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var item in items) foreach (var vertex in new[] { item.Triangle.A, item.Triangle.B, item.Triangle.C }) { min = Vector3.Min(min, vertex); max = Vector3.Max(max, vertex); }
        min -= new Vector3(Padding); max += new Vector3(Padding);
        var here = nodes.Count; nodes.Add(default);
        if (items.Count <= 8)
        {
            nodes[here] = new(min, max, 0xC0000000u | (uint)items.Count, (uint)ordered.Count);
            ordered.AddRange(items); return;
        }
        var extent = max - min; var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        items.Sort((a, b) => Component((a.Triangle.A + a.Triangle.B + a.Triangle.C) / 3, axis).CompareTo(Component((b.Triangle.A + b.Triangle.B + b.Triangle.C) / 3, axis)));
        var split = items.Count / 2;
        Build(items.GetRange(0, split), nodes, ordered);
        var right = nodes.Count;
        Build(items.GetRange(split, items.Count - split), nodes, ordered);
        nodes[here] = new(min, max, ((uint)axis << 30) | (uint)(right - here), 0);
    }

    public static void ValidateBvh(byte[] bytes, Triangle[] triangles)
    {
        if (bytes.Length == 0 || bytes.Length % 32 != 0) throw new InvalidDataException("Invalid BVH byte size.");
        var count = bytes.Length / 32; var seen = new bool[count]; var coverage = new int[triangles.Length];
        Node Read(int i) { var at = i * 32; Vector3 V(int p) => new(BitConverter.ToSingle(bytes, p), BitConverter.ToSingle(bytes, p + 4), BitConverter.ToSingle(bytes, p + 8)); return new(V(at), V(at + 16), BitConverter.ToUInt32(bytes, at + 12), BitConverter.ToUInt32(bytes, at + 28)); }
        bool Contains(Node node, Vector3 v) => v.X >= node.Min.X - .001f && v.Y >= node.Min.Y - .001f && v.Z >= node.Min.Z - .001f && v.X <= node.Max.X + .001f && v.Y <= node.Max.Y + .001f && v.Z <= node.Max.Z + .001f;
        void Visit(int i, int depth)
        {
            if (i < 0 || i >= count || seen[i] || depth > 128) throw new InvalidDataException("Invalid/cyclic BVH child reference.");
            seen[i] = true; var node = Read(i); var kind = node.Packed >> 30; var offset = node.Packed & 0x3FFFFFFFu;
            if (kind == 3)
            {
                if (offset == 0 || node.TriangleOffset + offset > triangles.Length) throw new InvalidDataException("Invalid BVH leaf triangle range.");
                for (var t = node.TriangleOffset; t < node.TriangleOffset + offset; t++) { coverage[t]++; var tri = triangles[t]; if (!Contains(node, tri.A) || !Contains(node, tri.B) || !Contains(node, tri.C)) throw new InvalidDataException("BVH leaf does not bound its triangle."); }
                return;
            }
            var left = i + 1; var right = checked(i + (int)offset);
            if (right >= count || left >= count) throw new InvalidDataException("BVH child outside array.");
            foreach (var child in new[] { Read(left), Read(right) }) if (!Contains(node, child.Min) || !Contains(node, child.Max)) throw new InvalidDataException("Parent BVH does not contain child.");
            Visit(left, depth + 1); Visit(right, depth + 1);
        }
        Visit(0, 0);
        if (seen.Any(v => !v) || coverage.Any(v => v != 1)) throw new InvalidDataException("BVH does not cover each node/triangle exactly once.");
    }

    public static bool Overlaps(Vector3 amin, Vector3 amax, Vector3 bmin, Vector3 bmax) => amin.X <= bmax.X && amax.X >= bmin.X && amin.Y <= bmax.Y && amax.Y >= bmin.Y && amin.Z <= bmax.Z && amax.Z >= bmin.Z;
    static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    static KVObject Sub(KVObject data, string key) => (KVObject)data.Properties[key].Value!;
    static KVObject Array(KVObject data, string key) => Sub(data, key);
    static Vector3 Vector(KVObject data, string key) { var a = Array(data, key).Properties.Values.Select(v => Convert.ToSingle(v.Value)).ToArray(); return new(a[0], a[1], a[2]); }
    static KVValue NumberArray(IEnumerable<object> values, KVType type) { var array = new KVObject(null, true, 0); var i = 0; foreach (var v in values) array.AddProperty((i++).ToString(), new(type, v)); return new(KVType.ARRAY, array); }
    static KVValue VectorValue(Vector3 value) => NumberArray(new object[] { (double)value.X, (double)value.Y, (double)value.Z }, KVType.DOUBLE);
    static void WriteVector(BinaryWriter writer, Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
}
