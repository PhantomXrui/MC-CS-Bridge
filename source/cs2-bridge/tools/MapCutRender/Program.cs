using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using MapPatchLab;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using KVObject = ValveResourceFormat.Serialization.KeyValues.KVObject;

namespace MapCutRender;

/// <summary>
/// Offline, reparse-verified render-mesh cutter. Reads extracted CS2 worldnode
/// .vmdl_c resources and degenerates every triangle whose three POSITION vertices
/// lie inside any one of the closed axis-aligned boxes of the run. Only the MBUF
/// block is rewritten; MDAT and every other block stay byte-for-byte identical.
/// Each model is parsed and written at most once per run. The game VPK is never touched.
/// </summary>
internal static class Program
{
    private const float DefaultEpsilon = 0.01f;

    // Production Inferno zones: B site, the original hull-free A patch, and the two
    // hull-free east A extensions. The 2135..2179 band stays uncut because its
    // straddling hulls cannot be removed by this pipeline.
    private static readonly CutBox[] DefaultBoxes =
    {
        new(new Vector3(172, 2566, -20), new Vector3(572, 2966, 180)),
        new(new Vector3(1925, 205, -20), new Vector3(2135, 655, 180)),
        new(new Vector3(2179, 205, -20), new Vector3(2379, 430, 180)),
        new(new Vector3(2179, 430, -20), new Vector3(2379, 655, 180)),
    };

    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        Options options;
        try
        {
            options = ParseOptions(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("argument error: " + ex.Message);
            PrintUsage();
            return 2;
        }

        if (!Directory.Exists(options.Source))
        {
            Console.Error.WriteLine("source directory does not exist: " + options.Source);
            return 2;
        }

        GuardOutputPath(options);

        var files = Directory.GetFiles(options.Source, "*.vmdl_c")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var results = new List<ModelResult>();
        var errors = new List<ModelError>();
        var warnings = new List<string>();
        var zoneTriangles = new int[options.Boxes.Count];

        Console.WriteLine("source: " + options.Source);
        Console.WriteLine("output: " + options.Output);
        Console.WriteLine($"cut zones: {options.Boxes.Count} epsilon={options.Epsilon}");
        for (var zone = 0; zone < options.Boxes.Count; zone++)
            Console.WriteLine($"  zone {zone + 1}: {options.Boxes[zone].Describe()}");

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            try
            {
                var result = ProcessModel(file, options, zoneTriangles);
                if (result != null)
                {
                    results.Add(result);
                    warnings.AddRange(result.Warnings.Select(warning => name + ": " + warning));
                    Console.WriteLine($"changed {name}: {result.TrianglesDegenerated} triangles" +
                                      (result.TrianglesDegeneratedByZone.Length == 0 ? string.Empty
                                          : " (" + string.Join(", ", result.TrianglesDegeneratedByZone.Select((count, zone) => $"zone {zone + 1}={count}")) + ")"));
                }
            }
            catch (VerificationException)
            {
                // A failed reparse must be loud, not a line in a report.
                throw;
            }
            catch (Exception ex)
            {
                var message = ex.GetType().Name + ": " + ex.Message;
                errors.Add(new ModelError(name, message));
                Console.Error.WriteLine("error " + name + ": " + message);
            }
        }

        var totalDegenerated = results.Sum(result => result.TrianglesDegenerated);
        Diagnostics? diagnostics = null;
        if (results.Count == 0)
        {
            var zeroHit = ComputeZeroHitDiagnostics(options, files);
            diagnostics = zeroHit;
            Console.WriteLine($"warning: no triangle has all three vertices inside any of the {options.Boxes.Count} cut zone(s); " +
                              $"nearest POSITION vertex [{string.Join(", ", zeroHit.NearestVertex.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)))}] is " +
                              $"{zeroHit.NearestDistance:F3} units away (zone {zeroHit.NearestZone + 1})");
            if (zeroHit.MinZInXyFootprint is { } minZ)
            {
                Console.WriteLine($"warning: {zeroHit.VerticesInXyFootprint} render vertices share a cut XY footprint; " +
                                  $"the lowest is at z={minZ:F3}");
            }
        }

        var zones = options.Boxes.Select((box, zone) => new ZoneResult(
            Zone: zone + 1,
            Min: new[] { box.Min.X, box.Min.Y, box.Min.Z },
            Max: new[] { box.Max.X, box.Max.Y, box.Max.Z },
            TrianglesDegenerated: zoneTriangles[zone])).ToArray();

        var report = new RenderCutReport(
            Status: errors.Count == 0 ? "ok" : "completed-with-errors",
            Map: "de_inferno",
            SourceDirectory: options.Source,
            OutputDirectory: options.Output,
            ZoneCount: options.Boxes.Count,
            Zones: zones,
            Epsilon: options.Epsilon,
            Rule: "all three triangle POSITION vertices inside any one epsilon-expanded AABB of the run",
            ModelsScanned: files.Length,
            ModelsChanged: results.Count,
            TotalTrianglesDegenerated: totalDegenerated,
            TotalErrors: errors.Count,
            Models: results,
            Errors: errors,
            Warnings: warnings.Distinct(StringComparer.Ordinal).ToArray(),
            ZeroHitDiagnostics: diagnostics,
            DryRun: options.DryRun,
            Tool: "MapCutRender (ValveResourceFormat 12.0.4261)");

        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Report));
        if (!string.IsNullOrEmpty(reportDirectory)) Directory.CreateDirectory(reportDirectory);
        File.WriteAllText(options.Report, JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine("report: " + options.Report);

        Console.WriteLine();
        Console.WriteLine("models scanned: " + files.Length);
        Console.WriteLine("models changed: " + results.Count);
        Console.WriteLine("triangles degenerated: " + totalDegenerated);
        for (var zone = 0; zone < zoneTriangles.Length; zone++)
            Console.WriteLine($"  zone {zone + 1} {options.Boxes[zone].Describe()}: {zoneTriangles[zone]} triangles");
        Console.WriteLine("errors: " + errors.Count);
        Console.WriteLine("patched files: " + (options.DryRun
            ? "(dry run, none written)"
            : results.Count == 0 ? "(none; no model changed)" : options.Output));

        if (errors.Count > 0)
        {
            Console.WriteLine("failed models:");
            foreach (var error in errors) Console.WriteLine("  " + error.Name + ": " + error.Error);
        }

        return errors.Count == 0 ? 0 : 1;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static ModelResult? ProcessModel(string path, Options options, int[] zoneTriangles)
    {
        var name = Path.GetFileName(path);
        var originalBytes = File.ReadAllBytes(path);
        using var resource = Parse(originalBytes, path);

        var mbuf = resource.GetBlockByType(BlockType.MBUF) as VBIB
            ?? throw new InvalidDataException("resource has no MBUF block");
        if (mbuf.VertexBuffers.Count == 0 || mbuf.IndexBuffers.Count == 0)
            throw new InvalidDataException("MBUF has no vertex/index buffers");
        if (resource.GetBlockByType(BlockType.MDAT) is not Mesh mesh)
            throw new InvalidDataException("resource has no MDAT mesh block");

        for (var i = 0; i < mbuf.VertexBuffers.Count; i++)
            ValidateVertexBuffer(mbuf.VertexBuffers[i], i);
        for (var i = 0; i < mbuf.IndexBuffers.Count; i++)
        {
            var ib = mbuf.IndexBuffers[i];
            if (ib.ElementSizeInBytes != 2)
                throw new NotSupportedException($"index buffer {i} has {ib.ElementSizeInBytes}-byte indices; 16-bit indices are required");
            if (ib.Data.Length != checked((int)ib.ElementCount * 2))
                throw new InvalidDataException($"index buffer {i} data size does not match element count");
        }

        var originalVertexBytes = mbuf.VertexBuffers.Select(buffer => buffer.Data.ToArray()).ToArray();
        var originalIndexBytes = mbuf.IndexBuffers.Select(buffer => buffer.Data.ToArray()).ToArray();
        var warnings = new List<string>();
        var segments = BuildSegments(mesh, mbuf, warnings);
        var changedTriangles = new HashSet<(int IndexBuffer, int TriangleStart)>();
        var changedIndices = new Dictionary<(int IndexBuffer, int Offset), ushort>();
        var changedByZone = new int[options.Boxes.Count];
        var trianglesScanned = 0;

        foreach (var segment in segments)
        {
            trianglesScanned += ProcessSegment(mbuf, segment, options, changedTriangles, changedIndices, changedByZone, warnings);
        }
        for (var zone = 0; zone < changedByZone.Length; zone++) zoneTriangles[zone] += changedByZone[zone];

        if (changedTriangles.Count == 0)
        {
            foreach (var warning in warnings)
                Console.Error.WriteLine($"warning {name}: {warning}");
            return null;
        }

        var container = ResourceContainer.Read(originalBytes);
        var mbufIndex = container.Blocks.FindIndex(block => block.Type == "MBUF");
        if (mbufIndex < 0) throw new InvalidDataException("container has no MBUF block");

        container.Blocks[mbufIndex] = new ResourceContainer.Block(
            "MBUF", MbufWriter.Write(mbuf, container.Blocks[mbufIndex].Bytes));
        var patchedBytes = container.Write();
        var target = Path.Combine(options.Output, name);
        var bytesToVerify = patchedBytes;
        if (!options.DryRun)
        {
            Directory.CreateDirectory(options.Output);
            File.WriteAllBytes(target, patchedBytes);
            // Re-read the artifact that was actually written, not just the memory copy.
            bytesToVerify = File.ReadAllBytes(target);
            if (!bytesToVerify.AsSpan().SequenceEqual(patchedBytes))
                throw new VerificationException("written file does not match the generated resource bytes");
        }

        VerifyPatch(path, originalBytes, bytesToVerify, mbuf, mbufIndex, segments,
            originalVertexBytes, originalIndexBytes, changedTriangles, changedIndices, options);

        foreach (var warning in warnings)
            Console.Error.WriteLine($"warning {name}: {warning}");

        return new ModelResult(
            Name: name,
            TrianglesScanned: trianglesScanned,
            TrianglesDegenerated: changedTriangles.Count,
            TrianglesDegeneratedByZone: changedByZone,
            VertexBuffers: mbuf.VertexBuffers.Count,
            IndexBuffers: mbuf.IndexBuffers.Count,
            Segments: segments.Count,
            OriginalBytes: originalBytes.Length,
            PatchedBytes: patchedBytes.Length,
            Sha256: Convert.ToHexString(SHA256.HashData(patchedBytes)),
            Written: !options.DryRun,
            Target: options.DryRun ? null : target,
            Warnings: warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void VerifyPatch(
        string path,
        byte[] originalBytes,
        byte[] patchedBytes,
        VBIB expected,
        int mbufIndex,
        IReadOnlyList<Segment> segments,
        byte[][] originalVertexBytes,
        byte[][] originalIndexBytes,
        HashSet<(int IndexBuffer, int TriangleStart)> changedTriangles,
        Dictionary<(int IndexBuffer, int Offset), ushort> changedIndices,
        Options options)
    {
        Resource reparsed;
        try
        {
            reparsed = Parse(patchedBytes, path);
        }
        catch (Exception ex)
        {
            throw new VerificationException("written resource does not parse with VRF 12: " + ex.Message, ex);
        }

        using (reparsed)
        {
            var check = reparsed.GetBlockByType(BlockType.MBUF) as VBIB
                ?? throw new VerificationException("written resource has no MBUF block");

            if (check.VertexBuffers.Count != expected.VertexBuffers.Count ||
                check.IndexBuffers.Count != expected.IndexBuffers.Count)
                throw new VerificationException("written MBUF buffer counts changed");

            for (var i = 0; i < expected.VertexBuffers.Count; i++)
            {
                var before = expected.VertexBuffers[i];
                var after = check.VertexBuffers[i];
                if (!LayoutEquals(before, after))
                    throw new VerificationException($"vertex buffer {i} layout changed in written MBUF");
                if (!after.Data.AsSpan().SequenceEqual(originalVertexBytes[i]))
                    throw new VerificationException($"vertex buffer {i} data changed in written MBUF");
            }

            for (var i = 0; i < expected.IndexBuffers.Count; i++)
            {
                var after = check.IndexBuffers[i];
                if (after.ElementCount != expected.IndexBuffers[i].ElementCount ||
                    after.ElementSizeInBytes != expected.IndexBuffers[i].ElementSizeInBytes)
                    throw new VerificationException($"index buffer {i} layout changed in written MBUF");
                if (!after.Data.AsSpan().SequenceEqual(expected.IndexBuffers[i].Data))
                    throw new VerificationException($"index buffer {i} data did not round-trip");
            }

            var originalContainer = ResourceContainer.Read(originalBytes);
            var patchedContainer = ResourceContainer.Read(patchedBytes);
            if (patchedContainer.HeaderVersion != originalContainer.HeaderVersion ||
                patchedContainer.ResourceVersion != originalContainer.ResourceVersion)
                throw new VerificationException("resource header version changed");
            if (!patchedContainer.TrailingBytes.AsSpan().SequenceEqual(originalContainer.TrailingBytes))
                throw new VerificationException("resource trailing bytes changed");
            if (patchedContainer.Blocks.Count != originalContainer.Blocks.Count)
                throw new VerificationException("resource block count changed");

            for (var i = 0; i < originalContainer.Blocks.Count; i++)
            {
                var before = originalContainer.Blocks[i];
                var after = patchedContainer.Blocks[i];
                if (before.Type != after.Type)
                    throw new VerificationException($"resource block {i} type changed ({before.Type} -> {after.Type})");
                if (i == mbufIndex)
                {
                    if (after.Bytes.AsSpan().SequenceEqual(before.Bytes))
                        throw new VerificationException("MBUF block bytes were not rewritten");
                }
                else if (!after.Bytes.AsSpan().SequenceEqual(before.Bytes))
                {
                    throw new VerificationException($"non-MBUF block {before.Type} changed");
                }
            }

            // The only index words allowed to move are the 2nd and 3rd index of a
            // triangle that was actually degenerated, and each must become index 0.
            for (var ibIndex = 0; ibIndex < originalIndexBytes.Length; ibIndex++)
            {
                var before = originalIndexBytes[ibIndex];
                var after = check.IndexBuffers[ibIndex].Data;
                if (before.Length != after.Length)
                    throw new VerificationException($"index buffer {ibIndex} byte length changed");
                for (var word = 0; word < before.Length / 2; word++)
                {
                    var oldValue = BinaryPrimitives.ReadUInt16LittleEndian(before.AsSpan(word * 2, 2));
                    var newValue = BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(word * 2, 2));
                    if (oldValue == newValue) continue;
                    if (!changedIndices.TryGetValue((ibIndex, word), out var expectedValue) || newValue != expectedValue)
                        throw new VerificationException($"unexpected index change in buffer {ibIndex} at index {word}");
                }
                foreach (var pair in changedIndices.Where(pair => pair.Key.IndexBuffer == ibIndex))
                {
                    var word = pair.Key.Offset;
                    var value = BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(word * 2, 2));
                    if (value != pair.Value)
                        throw new VerificationException($"degenerated index did not persist in buffer {ibIndex} at index {word}");
                }
            }

            // Reparsed geometry must contain no non-degenerate triangle entirely in the box.
            var reparsedTriangles = new HashSet<(int IndexBuffer, int TriangleStart)>();
            foreach (var segment in segments)
            {
                ScanSegment(check, segment, options, (indexBuffer, triangleStart, degenerate) =>
                {
                    if (!degenerate)
                        throw new VerificationException(
                            $"reparsed triangle {triangleStart} in index buffer {indexBuffer} is still non-degenerate inside the cut box");
                    reparsedTriangles.Add((indexBuffer, triangleStart));
                });
            }
            if (!reparsedTriangles.IsSupersetOf(changedTriangles))
                throw new VerificationException("a degenerated triangle is no longer completely inside the cut box after reparse");
        }

        GC.KeepAlive(expected);
    }

    private static List<Segment> BuildSegments(Mesh mesh, VBIB mbuf, List<string> warnings)
    {
        var uses = ReadDrawUses(mesh, mbuf, warnings);
        var segments = new List<Segment>();
        for (var ibIndex = 0; ibIndex < mbuf.IndexBuffers.Count; ibIndex++)
        {
            var ib = mbuf.IndexBuffers[ibIndex];
            var count = checked((int)ib.ElementCount);
            var matching = uses.Where(use => use.IndexBuffer == ibIndex).ToList();
            if (matching.Count == 0)
            {
                warnings.Add($"index buffer {ibIndex} is not referenced by any draw call; pairing it with vertex buffer 0");
                segments.Add(new Segment(ibIndex, 0, 0, count));
                continue;
            }

            var vertexBuffers = matching.Select(use => use.VertexBuffer).Distinct().OrderBy(index => index).ToArray();
            if (vertexBuffers.Length == 1)
            {
                segments.Add(new Segment(ibIndex, vertexBuffers[0], 0, count));
                continue;
            }

            warnings.Add($"index buffer {ibIndex} is referenced by vertex buffers [{string.Join(", ", vertexBuffers)}]; pairing each draw range with its own vertex buffer");
            var covered = new bool[count];
            foreach (var use in matching)
            {
                var start = Math.Clamp(use.Start, 0, count);
                var end = Math.Clamp(use.Start + use.Count, 0, count);
                if (start >= end) continue;
                segments.Add(new Segment(ibIndex, use.VertexBuffer, start, end - start));
                for (var i = start; i < end; i++) covered[i] = true;
            }

            for (var start = 0; start < count;)
            {
                if (covered[start]) { start++; continue; }
                var end = start;
                while (end < count && !covered[end]) end++;
                warnings.Add($"index buffer {ibIndex} range {start}..{end} has no draw call; pairing it with vertex buffer 0");
                segments.Add(new Segment(ibIndex, 0, start, end - start));
                start = end;
            }
        }
        return segments;
    }

    private static List<Segment> ReadDrawUses(Mesh mesh, VBIB mbuf, List<string> warnings)
    {
        var uses = new List<Segment>();
        var scenes = mesh.Data.GetArray<KVObject>("m_sceneObjects");
        if (scenes == null || scenes.Length == 0)
        {
            warnings.Add("MDAT has no scene objects; using vertex buffer 0 for every index buffer");
            return uses;
        }

        foreach (var scene in scenes)
        {
            var draws = scene.GetArray<KVObject>("m_drawCalls");
            if (draws == null) continue;
            foreach (var draw in draws)
            {
                if (!draw.ContainsKey("m_indexBuffer") || !draw.ContainsKey("m_vertexBuffers"))
                {
                    warnings.Add("a draw call has no m_indexBuffer/m_vertexBuffers; using vertex buffer 0 for its range");
                    continue;
                }

                var ibObject = draw.GetProperty<KVObject>("m_indexBuffer");
                var ibIndex = checked((int)GetInteger(ibObject, "m_hBuffer"));
                var vertexObjects = draw.GetArray<KVObject>("m_vertexBuffers");
                if (vertexObjects == null || vertexObjects.Length == 0)
                {
                    warnings.Add("a draw call has no vertex buffer; using vertex buffer 0");
                    continue;
                }
                if (vertexObjects.Length > 1)
                    warnings.Add($"a draw call references {vertexObjects.Length} vertex buffers; using the first");
                var vbIndex = checked((int)GetInteger(vertexObjects[0], "m_hBuffer"));
                var start = checked((int)GetInteger(draw, "m_nStartIndex"));
                var count = checked((int)GetInteger(draw, "m_nIndexCount"));

                if (ibIndex < 0 || ibIndex >= mbuf.IndexBuffers.Count ||
                    vbIndex < 0 || vbIndex >= mbuf.VertexBuffers.Count)
                {
                    warnings.Add($"a draw call references out-of-range buffers (index {ibIndex}, vertex {vbIndex}); ignored");
                    continue;
                }
                if (start < 0 || count < 0)
                {
                    warnings.Add($"a draw call has an invalid index range ({start}, {count}); ignored");
                    continue;
                }
                uses.Add(new Segment(ibIndex, vbIndex, start, count));
            }
        }

        if (uses.Count == 0)
            warnings.Add("MDAT has no usable draw calls; using vertex buffer 0 for every index buffer");
        return uses;
    }

    private static int ProcessSegment(
        VBIB mbuf,
        Segment segment,
        Options options,
        HashSet<(int IndexBuffer, int TriangleStart)> changedTriangles,
        Dictionary<(int IndexBuffer, int Offset), ushort> changedIndices,
        int[] changedByZone,
        List<string> warnings)
    {
        var ib = mbuf.IndexBuffers[segment.IndexBuffer];
        var vb = mbuf.VertexBuffers[segment.VertexBuffer];
        var position = GetPositionField(vb);
        var count = checked((int)ib.ElementCount);
        var start = Math.Clamp(segment.Start, 0, count);
        var end = Math.Clamp(segment.Start + segment.Count, 0, count);
        if (start >= end) return 0;
        if (start % 3 != 0)
            throw new InvalidDataException($"draw range starts at index {start}, which is not triangle-aligned");
        if ((end - start) % 3 != 0)
            warnings.Add($"draw range {start}..{end} is not a multiple of three; the trailing indices are left untouched");

        var stride = checked((int)vb.ElementSizeInBytes);
        var positionOffset = checked((int)position.Offset);
        var positions = vb.Data;
        var indices = ib.Data;
        var scanned = 0;
        var last = end - 2;
        for (var index = start; index < last; index += 3)
        {
            scanned++;
            var a = BinaryPrimitives.ReadUInt16LittleEndian(indices.AsSpan(index * 2, 2));
            var b = BinaryPrimitives.ReadUInt16LittleEndian(indices.AsSpan((index + 1) * 2, 2));
            var c = BinaryPrimitives.ReadUInt16LittleEndian(indices.AsSpan((index + 2) * 2, 2));
            if (a >= vb.ElementCount || b >= vb.ElementCount || c >= vb.ElementCount)
                throw new InvalidDataException($"triangle {index / 3} references a vertex outside buffer {segment.VertexBuffer}");

            if (a == b && b == c) continue;
            var pa = ReadPosition(positions, a, stride, positionOffset);
            var pb = ReadPosition(positions, b, stride, positionOffset);
            var pc = ReadPosition(positions, c, stride, positionOffset);
            var zone = InsideZone(pa, pb, pc, options);
            if (zone < 0) continue;

            BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan((index + 1) * 2, 2), a);
            BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan((index + 2) * 2, 2), a);
            changedTriangles.Add((segment.IndexBuffer, index));
            changedIndices[(segment.IndexBuffer, index + 1)] = a;
            changedIndices[(segment.IndexBuffer, index + 2)] = a;
            changedByZone[zone]++;
        }
        return scanned;
    }

    private static void ScanSegment(VBIB mbuf, Segment segment, Options options,
        Action<int, int, bool> onInsideTriangle)
    {
        var ib = mbuf.IndexBuffers[segment.IndexBuffer];
        var vb = mbuf.VertexBuffers[segment.VertexBuffer];
        var position = GetPositionField(vb);
        var count = checked((int)ib.ElementCount);
        var start = Math.Clamp(segment.Start, 0, count);
        var end = Math.Clamp(segment.Start + segment.Count, 0, count);
        if (start % 3 != 0) start -= start % 3;
        var stride = checked((int)vb.ElementSizeInBytes);
        var positionOffset = checked((int)position.Offset);
        for (var index = start; index + 2 < end; index += 3)
        {
            var a = BinaryPrimitives.ReadUInt16LittleEndian(ib.Data.AsSpan(index * 2, 2));
            var b = BinaryPrimitives.ReadUInt16LittleEndian(ib.Data.AsSpan((index + 1) * 2, 2));
            var c = BinaryPrimitives.ReadUInt16LittleEndian(ib.Data.AsSpan((index + 2) * 2, 2));
            if (a >= vb.ElementCount || b >= vb.ElementCount || c >= vb.ElementCount)
                throw new VerificationException($"reparsed triangle {index / 3} references a vertex outside its buffer");
            if (InsideZone(ReadPosition(vb.Data, a, stride, positionOffset),
                           ReadPosition(vb.Data, b, stride, positionOffset),
                           ReadPosition(vb.Data, c, stride, positionOffset), options) >= 0)
                onInsideTriangle(segment.IndexBuffer, index, a == b && b == c);
        }
    }

    /// <summary>Index of the first zone whose epsilon-expanded box contains all three vertices, else -1.</summary>
    private static int InsideZone(Vector3 a, Vector3 b, Vector3 c, Options options)
    {
        for (var zone = 0; zone < options.Boxes.Count; zone++)
        {
            var box = options.Boxes[zone];
            if (Inside(a, box, options.Epsilon) && Inside(b, box, options.Epsilon) && Inside(c, box, options.Epsilon))
                return zone;
        }
        return -1;
    }

    private static bool Inside(Vector3 position, CutBox box, float epsilon)
    {
        return position.X >= box.Min.X - epsilon && position.X <= box.Max.X + epsilon &&
               position.Y >= box.Min.Y - epsilon && position.Y <= box.Max.Y + epsilon &&
               position.Z >= box.Min.Z - epsilon && position.Z <= box.Max.Z + epsilon;
    }

    private static Vector3 ReadPosition(byte[] data, int vertex, int stride, int offset)
    {
        var span = data.AsSpan(vertex * stride + offset, 12);
        return new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(span),
            BinaryPrimitives.ReadSingleLittleEndian(span[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(span[8..]));
    }

    private static long GetInteger(KVObject obj, string key)
    {
        if (!obj.Properties.TryGetValue(key, out var value) || value.Value == null)
            throw new InvalidDataException("keyvalues object is missing integer property " + key);
        return Convert.ToInt64(value.Value, CultureInfo.InvariantCulture);
    }

    private static VBIB.RenderInputLayoutField GetPositionField(VBIB.OnDiskBufferData buffer)
    {
        var field = buffer.InputLayoutFields.FirstOrDefault(candidate =>
            candidate.SemanticName == "POSITION" && candidate.SemanticIndex == 0);
        if (field.SemanticName == null)
            throw new NotSupportedException("vertex buffer has no POSITION:0 attribute");
        if (field.Format != DXGI_FORMAT.R32G32B32_FLOAT)
            throw new NotSupportedException("POSITION:0 is " + field.Format + ", not R32G32B32_FLOAT");
        if (field.Offset + 12 > buffer.ElementSizeInBytes)
            throw new InvalidDataException("POSITION:0 lies outside the vertex stride");
        return field;
    }

    private static void ValidateVertexBuffer(VBIB.OnDiskBufferData buffer, int index)
    {
        if (buffer.ElementSizeInBytes == 0 || buffer.ElementCount == 0)
            throw new InvalidDataException($"vertex buffer {index} is empty");
        if (buffer.Data.Length != checked((int)buffer.ElementCount * (int)buffer.ElementSizeInBytes))
            throw new InvalidDataException($"vertex buffer {index} data size does not match element count");
        GetPositionField(buffer);
    }

    private static bool LayoutEquals(VBIB.OnDiskBufferData a, VBIB.OnDiskBufferData b)
    {
        if (a.ElementCount != b.ElementCount || a.ElementSizeInBytes != b.ElementSizeInBytes ||
            a.InputLayoutFields.Length != b.InputLayoutFields.Length)
            return false;
        for (var i = 0; i < a.InputLayoutFields.Length; i++)
        {
            var x = a.InputLayoutFields[i];
            var y = b.InputLayoutFields[i];
            if (x.SemanticName != y.SemanticName || x.SemanticIndex != y.SemanticIndex ||
                x.Format != y.Format || x.Offset != y.Offset || x.Slot != y.Slot ||
                x.SlotType != y.SlotType || x.InstanceStepRate != y.InstanceStepRate)
                return false;
        }
        return true;
    }

    private static Diagnostics ComputeZeroHitDiagnostics(Options options, IReadOnlyList<string> files)
    {
        var nearest = Vector3.Zero;
        var nearestDistance = double.PositiveInfinity;
        var nearestZone = 0;
        var inFootprint = 0;
        float? minZ = null;
        foreach (var file in files)
        {
            try
            {
                using var resource = Parse(File.ReadAllBytes(file), file);
                if (resource.GetBlockByType(BlockType.MBUF) is not VBIB mbuf || mbuf.VertexBuffers.Count == 0)
                    continue;
                var vb = mbuf.VertexBuffers[0];
                var field = GetPositionField(vb);
                var stride = checked((int)vb.ElementSizeInBytes);
                var offset = checked((int)field.Offset);
                for (var vertex = 0; vertex < vb.ElementCount; vertex++)
                {
                    var position = ReadPosition(vb.Data, vertex, stride, offset);
                    for (var zone = 0; zone < options.Boxes.Count; zone++)
                    {
                        var box = options.Boxes[zone];
                        var clamped = Vector3.Clamp(position, box.Min, box.Max);
                        var distance = Vector3.Distance(position, clamped);
                        if (distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearest = position;
                            nearestZone = zone;
                        }
                        if (position.X >= box.Min.X && position.X <= box.Max.X &&
                            position.Y >= box.Min.Y && position.Y <= box.Max.Y)
                        {
                            inFootprint++;
                            if (minZ == null || position.Z < minZ) minZ = position.Z;
                        }
                    }
                }
            }
            catch
            {
                // The main pass already reported parse errors; diagnostics are best effort.
            }
        }

        string? note = null;
        if (double.IsPositiveInfinity(nearestDistance))
        {
            note = "No readable POSITION data was found.";
        }
        else if (nearestDistance > options.Epsilon)
        {
            note = $"No triangle had all three vertices inside any cut zone. The nearest render vertex is " +
                   $"{nearestDistance:F3} units outside zone {nearestZone + 1}, so no zone encloses render geometry.";
        }

        return new Diagnostics(
            NearestVertex: new[] { nearest.X, nearest.Y, nearest.Z },
            NearestDistance: double.IsPositiveInfinity(nearestDistance) ? null : nearestDistance,
            NearestZone: nearestZone,
            VerticesInXyFootprint: inFootprint,
            MinZInXyFootprint: minZ,
            Note: note);
    }

    private static Resource Parse(byte[] bytes, string path)
    {
        var resource = new Resource { FileName = path };
        resource.Read(new MemoryStream(bytes, false), false);
        return resource;
    }

    private static Options ParseOptions(string[] args)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("MCCS_ROOT");
        if (string.IsNullOrWhiteSpace(configuredRoot) || !Path.IsPathFullyQualified(configuredRoot))
            throw new ArgumentException("Set MCCS_ROOT to an absolute local bridge workspace path.");
        var root = Path.GetFullPath(configuredRoot);
        var source = Path.Combine(root, "build-cache", "inferno-vmdl");
        var output = Path.Combine(root, "build-cache", "map-patch-de_inferno", "variant", "maps", "de_inferno", "worldnodes");
        var report = Path.Combine(root, "build-cache", "map-patch-de_inferno", "render-cut-report.json");
        var boxes = new List<CutBox>();
        var explicitBoxes = false;
        Vector3? pendingMin = null;
        var epsilon = DefaultEpsilon;
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--src": source = args[++i]; break;
                case "--out": output = args[++i]; break;
                case "--report": report = args[++i]; break;
                case "--box": boxes.Add(ParseBox(args[++i])); explicitBoxes = true; break;
                case "--min":
                    if (pendingMin != null) throw new ArgumentException("--min must be followed by --max before the next --min");
                    pendingMin = ParseVector(args[++i]); break;
                case "--max":
                    if (pendingMin == null) throw new ArgumentException("--max must follow its --min");
                    boxes.Add(new CutBox(pendingMin.Value, ParseVector(args[++i]))); pendingMin = null; explicitBoxes = true; break;
                case "--epsilon": epsilon = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--dry-run": dryRun = true; break;
                default: throw new ArgumentException("unknown argument: " + args[i]);
            }
        }

        if (pendingMin != null) throw new ArgumentException("--min is missing its --max");
        if (!float.IsFinite(epsilon) || epsilon < 0) throw new ArgumentException("--epsilon must be finite and non-negative");
        if (!explicitBoxes) boxes.AddRange(DefaultBoxes);
        foreach (var box in boxes)
            if (!float.IsFinite(box.Min.X) || !float.IsFinite(box.Min.Y) || !float.IsFinite(box.Min.Z) ||
                !float.IsFinite(box.Max.X) || !float.IsFinite(box.Max.Y) || !float.IsFinite(box.Max.Z))
                throw new ArgumentException("--min/--max must be finite");
        CutBox.Validate(boxes);

        return new Options(root, source, output, report, boxes, epsilon, dryRun);
    }

    private static CutBox ParseBox(string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 6) throw new ArgumentException("expected minx,miny,minz,maxx,maxy,maxz: " + text);
        var values = parts.Select(part => float.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        return new CutBox(new Vector3(values[0], values[1], values[2]), new Vector3(values[3], values[4], values[5]));
    }

    private static Vector3 ParseVector(string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 3) throw new ArgumentException("expected x,y,z: " + text);
        return new Vector3(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture),
            float.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    private static void GuardOutputPath(Options options)
    {
        var buildCacheRoot = Path.Combine(options.Root, "build-cache");
        var buildCache = Path.GetFullPath(buildCacheRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var output = Path.GetFullPath(options.Output);
        var report = Path.GetFullPath(options.Report);
        if (!output.StartsWith(buildCache, StringComparison.OrdinalIgnoreCase) ||
            !report.StartsWith(buildCache, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("output and report paths must stay under " + buildCacheRoot);
        var gameRoot = Path.GetFullPath(Path.Combine(options.Root, "games")) + Path.DirectorySeparatorChar;
        if (output.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase) ||
            report.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("refusing to write into the game directory");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("MapCutRender - degenerate render triangles inside one or more CS2 world-space boxes");
        Console.WriteLine("  MCCS_ROOT         required absolute local workspace path (except --help)");
        Console.WriteLine("  --src <dir>        extracted .vmdl_c directory (default: MCCS_ROOT/build-cache/inferno-vmdl)");
        Console.WriteLine("  --out <dir>        patched resources (default: MCCS_ROOT/build-cache/map-patch-de_inferno/variant/maps/de_inferno/worldnodes)");
        Console.WriteLine("  --report <file>    JSON report (default: MCCS_ROOT/build-cache/map-patch-de_inferno/render-cut-report.json)");
        Console.WriteLine("  --box minx,miny,minz,maxx,maxy,maxz");
        Console.WriteLine("                     add a cut zone; repeat for every zone (may be mixed with --min/--max)");
        Console.WriteLine("  --min x,y,z        cut zone minimum, must be followed by --max (repeatable)");
        Console.WriteLine("  --max x,y,z        cut zone maximum");
        Console.WriteLine("  --epsilon <units>  box expansion (default: " + DefaultEpsilon + ")");
        Console.WriteLine("  --dry-run          scan and verify without writing resources");
        Console.WriteLine("  default zones: " + string.Join(" | ", DefaultBoxes.Select(box => "[" + box.Describe() + "]")));
        Console.WriteLine("  every model is parsed and written at most once per run; a triangle is degenerated");
        Console.WriteLine("  when all three POSITION vertices are inside any one zone.");
    }

    private sealed record Options(string Root, string Source, string Output, string Report, IReadOnlyList<CutBox> Boxes, float Epsilon, bool DryRun);

    private sealed record Segment(int IndexBuffer, int VertexBuffer, int Start, int Count);

    private sealed record ZoneResult(int Zone, float[] Min, float[] Max, int TrianglesDegenerated);

    private sealed record ModelResult(
        string Name,
        int TrianglesScanned,
        int TrianglesDegenerated,
        int[] TrianglesDegeneratedByZone,
        int VertexBuffers,
        int IndexBuffers,
        int Segments,
        int OriginalBytes,
        int PatchedBytes,
        string Sha256,
        bool Written,
        string? Target,
        string[] Warnings);

    private sealed record ModelError(string Name, string Error);

    private sealed record Diagnostics(
        float[] NearestVertex,
        double? NearestDistance,
        int NearestZone,
        int VerticesInXyFootprint,
        float? MinZInXyFootprint,
        string? Note);

    private sealed record RenderCutReport(
        string Status,
        string Map,
        string SourceDirectory,
        string OutputDirectory,
        int ZoneCount,
        IReadOnlyList<ZoneResult> Zones,
        float Epsilon,
        string Rule,
        int ModelsScanned,
        int ModelsChanged,
        int TotalTrianglesDegenerated,
        int TotalErrors,
        IReadOnlyList<ModelResult> Models,
        IReadOnlyList<ModelError> Errors,
        IReadOnlyList<string> Warnings,
        Diagnostics? ZeroHitDiagnostics,
        bool DryRun,
        string Tool);

    private sealed class VerificationException : Exception
    {
        public VerificationException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
