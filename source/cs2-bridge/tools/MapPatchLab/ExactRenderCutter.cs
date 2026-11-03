using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace MapPatchLab;

/// <summary>
/// Offline exact render-surface subtraction for the inspected old CS2 MDAT/MBUF
/// format. Uses the Source 2 layouts documented by ValveResourceFormat
/// (https://github.com/ValveResourceFormat/ValveResourceFormat, MIT).
/// This does not establish engine acceptance and does not add pit side/bottom caps.
/// New positions, UVs and UNORM channels are interpolated. New packed normal/tangent
/// frames use an original vertex's valid encoded value, a declared shading approximation.
/// </summary>
public static class ExactRenderCutter
{
    private const int Stride = 32, MeshletIndices = 144, MeshletTriangles = 48;
    public sealed record Report(int OriginalVertices, int OutputVertices, int OriginalIndices, int OutputIndices,
        int OriginalMeshlets, int OutputMeshlets, int ChangedTriangles, int ChangedMeshlets, int NewAttributeVertices,
        double OriginalArea, double OutputArea, double ExpectedRemovedArea, double ActualRemovedArea,
        bool OriginalVertexBytesPreserved, bool OtherBlocksPreserved, bool ReparsePassed, string ShadingPolicy);
    public sealed record Result(byte[] Bytes, Report Report);
    private readonly record struct IndexedTriangle(ushort A, ushort B, ushort C);

    public static Result Cut(byte[] original, Vector3 min, Vector3 max) =>
        Cut(original, new[] { new CutBox(min, max) });

    /// <summary>
    /// Subtracts every box from the same render mesh, in the order given. A triangle is
    /// clipped against each box only when that box actually intersects it, so a run over
    /// disjoint zones touches only the surface of those zones.
    /// </summary>
    public static Result Cut(byte[] original, IReadOnlyList<CutBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(original);
        CutBox.Validate(boxes);
        // Validate bounds even if no triangle is affected.
        foreach (var box in boxes) TriangleClipper.SubtractBox(new(Vector3.Zero, Vector3.UnitX, Vector3.UnitY), box.Min, box.Max);
        var container = ResourceContainer.Read(original);
        using var resource = Parse(original);
        if (resource.Blocks.Any(b => b.Type.ToString() == "MSLT")) throw new NotSupportedException("New MSLT meshlets are not supported by this old-format cutter.");
        var mesh = (Mesh)resource.Blocks.Single(b => b.Type.ToString() == "MDAT");
        var buffers = (VBIB)resource.Blocks.Single(b => b.Type.ToString() == "MBUF");
        if (buffers.VertexBuffers.Count != 1 || buffers.IndexBuffers.Count != 1) throw new NotSupportedException("Expected one vertex and index buffer.");
        var vertexBuffer = buffers.VertexBuffers[0]; var indexBuffer = buffers.IndexBuffers[0];
        ValidateLayout(vertexBuffer, indexBuffer);
        var originalVertexBytes = vertexBuffer.Data.ToArray();
        var vertices = new VertexBuilder(originalVertexBytes);
        var scenes = Object(mesh.Data, "m_sceneObjects");
        if (!scenes.IsArray || scenes.Properties.Count != 1) throw new NotSupportedException("Expected one scene object.");
        var scene = Object(scenes, "0"); var draws = Object(scene, "m_drawCalls");
        var oldMeshlets = Object(scene, "m_meshlets"); var drawBounds = Object(scene, "m_drawBounds");
        if (draws.Properties.Count != drawBounds.Properties.Count) throw new InvalidDataException("Draw bounds do not match draw calls.");
        var newMeshlets = new KVObject("m_meshlets", true);
        var newIndices = new List<ushort>();
        int oldIndexCursor = 0, oldMeshletCursor = 0, changedTriangles = 0, changedMeshlets = 0;
        double originalArea = 0, outputArea = 0, removedArea = 0;
        for (var drawIndex = 0; drawIndex < draws.Properties.Count; drawIndex++)
        {
            var draw = Object(draws, drawIndex.ToString(CultureInfo.InvariantCulture));
            ValidateDraw(draw);
            var start = Integer(draw, "m_nStartIndex"); var count = Integer(draw, "m_nIndexCount");
            var first = Integer(draw, "m_nFirstMeshlet"); var number = Integer(draw, "m_nNumMeshlets");
            if (start != oldIndexCursor || first != oldMeshletCursor || count != number * MeshletIndices)
                throw new NotSupportedException("Expected contiguous 48-triangle meshlet groups.");
            if (start < 0 || count < 0 || (long)start + count > indexBuffer.ElementCount || (long)first + number > oldMeshlets.Properties.Count)
                throw new InvalidDataException("Draw range exceeds its buffer.");
            var newStart = newIndices.Count; var newFirst = newMeshlets.Properties.Count;
            for (var offset = 0; offset < number; offset++)
            {
                var sourceDescriptor = oldMeshlets.Properties[(first + offset).ToString(CultureInfo.InvariantCulture)];
                var descriptor = (KVObject)sourceDescriptor.Value;
                if (descriptor.Properties.Count != 2 || !descriptor.Properties.ContainsKey("m_PackedAABB") || !descriptor.Properties.ContainsKey("m_CullingData"))
                    throw new NotSupportedException("Only old meshlet bounds/cone descriptors have been inspected.");
                var kept = new List<IndexedTriangle>(); var meshletChanged = false;
                var firstIndex = start + offset * MeshletIndices;
                ushort ReadIndex(int at) => BinaryPrimitives.ReadUInt16LittleEndian(indexBuffer.Data.AsSpan(at * 2, 2));
                for (var j = 0; j < MeshletIndices; j += 3)
                {
                    var ids = new IndexedTriangle(ReadIndex(firstIndex + j), ReadIndex(firstIndex + j + 1), ReadIndex(firstIndex + j + 2));
                    var triangle = new Triangle(vertices.Position(ids.A), vertices.Position(ids.B), vertices.Position(ids.C));
                    var area = TriangleClipper.Area(triangle); originalArea += area;
                    var intersections = boxes.Select(box => TriangleClipper.IntersectionArea(triangle, box.Min, box.Max)).ToArray();
                    var intersection = intersections.Sum(); removedArea += intersection;
                    if (intersections.All(value => value <= 5e-11))
                    {
                        // Preserve all original triangles, including existing padding, exactly.
                        kept.Add(ids); outputArea += area; continue;
                    }
                    meshletChanged = true; changedTriangles++;
                    IReadOnlyList<Triangle> pieces = new[] { triangle };
                    for (var boxIndex = 0; boxIndex < boxes.Count; boxIndex++)
                    {
                        if (intersections[boxIndex] <= 5e-11) continue;
                        var next = new List<Triangle>();
                        foreach (var piece in pieces) next.AddRange(TriangleClipper.SubtractBox(piece, boxes[boxIndex].Min, boxes[boxIndex].Max));
                        pieces = next;
                    }
                    var retainedArea = pieces.Sum(TriangleClipper.Area); outputArea += retainedArea;
                    if (Math.Abs(area - intersection - retainedArea) > Math.Max(.005, area * 5e-6))
                        throw new InvalidDataException("Clipping changed positive area outside the box.");
                    foreach (var part in pieces)
                    {
                        foreach (var box in boxes)
                            if (TriangleClipper.IntersectionArea(part, box.Min, box.Max) > .002)
                                throw new InvalidDataException("Clipped triangle retains positive area inside a box.");
                        kept.Add(new(vertices.Interpolate(ids, triangle, part.A), vertices.Interpolate(ids, triangle, part.B), vertices.Interpolate(ids, triangle, part.C)));
                    }
                }
                if (!meshletChanged)
                {
                    AddMeshlet(kept, sourceDescriptor, newMeshlets, newIndices);
                    continue;
                }
                changedMeshlets++;
                // Keep descendants in the same source meshlet and draw call. Its
                // original packed bounds and face-normal cone remain conservative:
                // clipping adds no positions outside the original triangles.
                var chunk = new List<IndexedTriangle>(); var unique = new HashSet<ushort>();
                foreach (var tri in kept)
                {
                    if (tri.A == tri.B || tri.B == tri.C || tri.A == tri.C) continue;
                    var extra = new[] { tri.A, tri.B, tri.C }.Count(id => !unique.Contains(id));
                    if (chunk.Count == MeshletTriangles || unique.Count + extra > 64)
                    {
                        AddMeshlet(chunk, sourceDescriptor, newMeshlets, newIndices); chunk.Clear(); unique.Clear();
                    }
                    chunk.Add(tri); unique.Add(tri.A); unique.Add(tri.B); unique.Add(tri.C);
                }
                if (chunk.Count > 0) AddMeshlet(chunk, sourceDescriptor, newMeshlets, newIndices);
                else if (kept.All(t => t.A == t.B || t.B == t.C || t.A == t.C))
                    AddMeshlet(new[] { new IndexedTriangle(ReadIndex(firstIndex), ReadIndex(firstIndex), ReadIndex(firstIndex)) }, sourceDescriptor, newMeshlets, newIndices);
            }
            SetInteger(draw, "m_nStartIndex", newStart);
            SetInteger(draw, "m_nIndexCount", newIndices.Count - newStart);
            SetInteger(draw, "m_nFirstMeshlet", newFirst);
            SetInteger(draw, "m_nNumMeshlets", newMeshlets.Properties.Count - newFirst);
            // In this old format with BaseVertex=0 this is max referenced index+1,
            // not the number of unique vertices. Preserve unused original prefix.
            SetInteger(draw, "m_nVertexCount", newIndices.Skip(newStart).Select(i => (int)i).DefaultIfEmpty(-1).Max() + 1);
            oldIndexCursor += count; oldMeshletCursor += number;
        }
        if (oldIndexCursor != indexBuffer.ElementCount || oldMeshletCursor != oldMeshlets.Properties.Count)
            throw new InvalidDataException("Draws did not cover the entire original buffer.");
        if (changedTriangles == 0) throw new InvalidDataException("The selected render resource has no positive-area intersection.");
        if (Math.Abs(originalArea - outputArea - removedArea) > Math.Max(.02, removedArea * 1e-5))
            throw new InvalidDataException("Global area conservation failed.");
        scene.Properties["m_meshlets"] = new KVValue(KVType.ARRAY, newMeshlets);
        vertexBuffer.Data = vertices.Bytes(); vertexBuffer.ElementCount = checked((uint)(vertexBuffer.Data.Length / Stride));
        indexBuffer.Data = new byte[checked(newIndices.Count * 2)]; indexBuffer.ElementCount = checked((uint)newIndices.Count);
        for (var i = 0; i < newIndices.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(indexBuffer.Data.AsSpan(i * 2, 2), newIndices[i]);
        buffers.VertexBuffers[0] = vertexBuffer; buffers.IndexBuffers[0] = indexBuffer;
        var mdatIndex = container.Blocks.FindIndex(b => b.Type == "MDAT");
        var mbufIndex = container.Blocks.FindIndex(b => b.Type == "MBUF");
        var mdat = container.Blocks[mdatIndex].Bytes;
        if (BitConverter.ToUInt32(mdat) != Kv3V4Writer.Magic) throw new NotSupportedException("Expected KV3 v4 MDAT.");
        container.Blocks[mdatIndex] = new("MDAT", Kv3V4Writer.Write(mesh.Data, new Guid(mdat.AsSpan(4, 16))));
        container.Blocks[mbufIndex] = new("MBUF", MbufWriter.Write(buffers, container.Blocks[mbufIndex].Bytes));
        var bytes = container.Write();
        using var reparsed = Parse(bytes);
        Kv3V4Writer.AssertEquivalent(mesh.Data, ((Mesh)reparsed.Blocks.Single(b => b.Type.ToString() == "MDAT")).Data);
        var checkBuffers = (VBIB)reparsed.Blocks.Single(b => b.Type.ToString() == "MBUF");
        if (!vertexBuffer.Data.SequenceEqual(checkBuffers.VertexBuffers.Single().Data) || !indexBuffer.Data.SequenceEqual(checkBuffers.IndexBuffers.Single().Data))
            throw new InvalidDataException("Written vertex/index data did not roundtrip.");
        if (!vertexBuffer.Data.AsSpan(0, originalVertexBytes.Length).SequenceEqual(originalVertexBytes)) throw new InvalidDataException("Original vertices changed.");
        var checkContainer = ResourceContainer.Read(bytes); var originalContainer = ResourceContainer.Read(original);
        for (var i = 0; i < container.Blocks.Count; i++)
            if (i != mdatIndex && i != mbufIndex && !checkContainer.Blocks[i].Bytes.SequenceEqual(originalContainer.Blocks[i].Bytes))
                throw new InvalidDataException("An unrelated render resource block changed.");
        return new(bytes, new(originalVertexBytes.Length / Stride, checked((int)vertexBuffer.ElementCount), oldIndexCursor, newIndices.Count,
            oldMeshlets.Properties.Count, newMeshlets.Properties.Count, changedTriangles, changedMeshlets, vertices.Added,
            originalArea, outputArea, removedArea, originalArea - outputArea, true, true, true,
            "New packed normal/tangent frames copy the original vertex with greatest barycentric weight; local shading is approximate. Positions, UV0, lightmap UV3 and UNORM RGBA4 are interpolated. No new pit caps."));
    }

    private static void AddMeshlet(IReadOnlyList<IndexedTriangle> triangles, KVValue source, KVObject descriptors, List<ushort> indices)
    {
        if (triangles.Count == 0 || triangles.Count > MeshletTriangles) throw new InvalidDataException("Invalid meshlet size.");
        descriptors.AddProperty("", source);
        foreach (var t in triangles) { indices.Add(t.A); indices.Add(t.B); indices.Add(t.C); }
        var pad = triangles[0].A;
        for (var i = triangles.Count; i < MeshletTriangles; i++) { indices.Add(pad); indices.Add(pad); indices.Add(pad); }
    }

    private static void ValidateLayout(VBIB.OnDiskBufferData vb, VBIB.OnDiskBufferData ib)
    {
        if (vb.ElementSizeInBytes != Stride || ib.ElementSizeInBytes != 2 || vb.ElementCount > ushort.MaxValue || vb.Data.Length != vb.ElementCount * Stride || ib.Data.Length != ib.ElementCount * 2)
            throw new NotSupportedException("Expected stride32 vertices and uint16 indices.");
        var expected = new[] { "POSITION:0:R32G32B32_FLOAT:0", "TEXCOORD:0:R32G32_FLOAT:12", "TEXCOORD:3:R16G16_UNORM:20", "NORMAL:0:R32_UINT:24", "TEXCOORD:4:R8G8B8A8_UNORM:28" };
        var actual = vb.InputLayoutFields.Select(f => $"{f.SemanticName}:{f.SemanticIndex}:{f.Format}:{f.Offset}").ToArray();
        if (!expected.SequenceEqual(actual) || vb.InputLayoutFields.Any(f => f.Slot != 0 || f.InstanceStepRate != 0 || f.SlotType.ToString() != "RENDER_SLOT_PER_VERTEX"))
            throw new NotSupportedException("Uninspected vertex attributes.");
    }
    private static void ValidateDraw(KVObject draw)
    {
        if ((string)draw.Properties["m_nPrimitiveType"].Value != "RENDER_PRIM_TRIANGLES" || Integer(draw, "m_nBaseVertex") != 0)
            throw new NotSupportedException("Only base-zero triangle draws are supported.");
        var ib = Object(draw, "m_indexBuffer"); var vbs = Object(draw, "m_vertexBuffers");
        if (Integer(ib, "m_hBuffer") != 0 || Integer(ib, "m_nBindOffsetBytes") != 0 || vbs.Properties.Count != 1)
            throw new NotSupportedException("Uninspected buffer bindings.");
        var vb = Object(vbs, "0");
        if (Integer(vb, "m_hBuffer") != 0 || Integer(vb, "m_nBindOffsetBytes") != 0) throw new NotSupportedException("Uninspected vertex binding.");
    }
    private static Resource Parse(byte[] bytes) { var r = new Resource(); r.Read(new MemoryStream(bytes), false); return r; }
    private static KVObject Object(KVObject parent, string name) => (KVObject)parent.Properties[name].Value;
    private static int Integer(KVObject parent, string name) => Convert.ToInt32(parent.Properties[name].Value, CultureInfo.InvariantCulture);
    private static void SetInteger(KVObject parent, string name, int value)
    {
        var old = parent.Properties[name];
        object number = old.Type == KVType.UINT64 ? (object)checked((ulong)value) : (long)value;
        parent.Properties[name] = old is KVFlaggedValue f ? new KVFlaggedValue(old.Type, f.Flag, number) : new KVValue(old.Type, number);
    }

    private sealed class VertexBuilder
    {
        private readonly byte[] original;
        private readonly MemoryStream all = new();
        private readonly Dictionary<string, ushort> appended = new(StringComparer.Ordinal);
        public int Added => appended.Count;
        public VertexBuilder(byte[] source) { original = source; all.Write(source); }
        public byte[] Bytes() => all.ToArray();
        public Vector3 Position(ushort id)
        {
            var at = id * Stride;
            if (at + Stride > original.Length) throw new InvalidDataException("Original index out of bounds.");
            return new(BitConverter.ToSingle(original, at), BitConverter.ToSingle(original, at + 4), BitConverter.ToSingle(original, at + 8));
        }
        public ushort Interpolate(IndexedTriangle ids, Triangle tri, Vector3 point)
        {
            if (point == tri.A) return ids.A; if (point == tri.B) return ids.B; if (point == tri.C) return ids.C;
            var weights = Barycentric(tri, point);
            var source = new[] { ids.A, ids.B, ids.C };
            var strongest = weights[1] > weights[0] ? 1 : 0; if (weights[2] > weights[strongest]) strongest = 2;
            var output = original.AsSpan(source[strongest] * Stride, Stride).ToArray();
            void Float(int at, float value) { if (!float.IsFinite(value)) throw new InvalidDataException("Nonfinite interpolated attribute."); BitConverter.TryWriteBytes(output.AsSpan(at, 4), value); }
            double Mix(Func<int, double> read) => weights[0] * read(source[0] * Stride) + weights[1] * read(source[1] * Stride) + weights[2] * read(source[2] * Stride);
            Float(0, point.X); Float(4, point.Y); Float(8, point.Z);
            foreach (var at in new[] { 12, 16 }) Float(at, (float)Mix(start => BitConverter.ToSingle(original, start + at)));
            foreach (var at in new[] { 20, 22 }) BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(at, 2), (ushort)Math.Clamp(Math.Round(Mix(start => BitConverter.ToUInt16(original, start + at))), 0, ushort.MaxValue));
            for (var at = 28; at < 32; at++) output[at] = (byte)Math.Clamp(Math.Round(Mix(start => original[start + at])), 0, byte.MaxValue);
            var key = Convert.ToHexString(output);
            if (appended.TryGetValue(key, out var existing)) return existing;
            var count = all.Length / Stride;
            if (count > ushort.MaxValue) throw new NotSupportedException("Clipping exceeded uint16 vertex capacity.");
            var id = checked((ushort)count); all.Write(output); appended.Add(key, id); return id;
        }
        private static double[] Barycentric(Triangle tri, Vector3 point)
        {
            // Double precision cross products; project onto the original triangle's
            // dominant plane so skinny/sloped triangles retain usable weights.
            double[] a = [tri.A.X, tri.A.Y, tri.A.Z], b = [tri.B.X, tri.B.Y, tri.B.Z], c = [tri.C.X, tri.C.Y, tri.C.Z], p = [point.X, point.Y, point.Z];
            var n = Vector3.Cross(tri.B - tri.A, tri.C - tri.A); var absolute = Vector3.Abs(n);
            var skip = absolute.X >= absolute.Y && absolute.X >= absolute.Z ? 0 : absolute.Y >= absolute.Z ? 1 : 2;
            var x = (skip + 1) % 3; var y = (skip + 2) % 3;
            var denominator = (b[y] - c[y]) * (a[x] - c[x]) + (c[x] - b[x]) * (a[y] - c[y]);
            if (Math.Abs(denominator) < 1e-12) throw new InvalidDataException("Degenerate interpolation triangle.");
            var wa = ((b[y] - c[y]) * (p[x] - c[x]) + (c[x] - b[x]) * (p[y] - c[y])) / denominator;
            var wb = ((c[y] - a[y]) * (p[x] - c[x]) + (a[x] - c[x]) * (p[y] - c[y])) / denominator;
            double[] weights = [wa, wb, 1 - wa - wb];
            if (weights.Any(w => !double.IsFinite(w) || w < -.001 || w > 1.001)) throw new InvalidDataException("New vertex lies outside its source triangle.");
            for (var i = 0; i < 3; i++) weights[i] = Math.Clamp(weights[i], 0, 1);
            var sum = weights.Sum(); for (var i = 0; i < 3; i++) weights[i] /= sum;
            var reconstructed = new Vector3((float)(weights[0] * a[0] + weights[1] * b[0] + weights[2] * c[0]), (float)(weights[0] * a[1] + weights[1] * b[1] + weights[2] * c[1]), (float)(weights[0] * a[2] + weights[1] * b[2] + weights[2] * c[2]));
            if (Vector3.Distance(point, reconstructed) > .002f) throw new InvalidDataException("New vertex left its original triangle plane.");
            return weights;
        }
    }
}
