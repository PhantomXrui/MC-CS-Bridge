using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace MapPatchLab;

public static class MapNamespaceBuilder
{
    public static List<string> UnrenamedV5 { get; } = new();
    public static string OldMapName { get; set; } = "de_dust2";
    public static string NewMapName { get; set; } = "de_dust2_mccs";
    static Regex NamespacePattern => new(@"maps([/\\])" + Regex.Escape(OldMapName) + @"(?=$|[/\\.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static string Rename(string text) => NamespacePattern.Replace(text, m => "maps" + m.Groups[1].Value + NewMapName);
    static bool HasOldPath(byte[] bytes) => Contains(bytes, Encoding.UTF8.GetBytes("maps/" + OldMapName + "/")) || Contains(bytes, Encoding.UTF8.GetBytes("maps\\" + OldMapName + "\\")) || Contains(bytes, Encoding.UTF8.GetBytes("maps/" + OldMapName + ".")) || Contains(bytes, Encoding.UTF8.GetBytes("maps\\" + OldMapName + "."));
    static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
    sealed record Reference(ulong Id, string Name);

    // RERL is an ID-to-name table. Keep IDs so opaque resource fields still refer
    // to their existing table entries; rename the target names consistently.
    // Public format source: VRF ResourceExtRefList.cs. Engine cache behavior is
    // intentionally left as a required cold-load acceptance test.
    static byte[] RenameReferences(byte[] bytes, List<string> references, ref int changes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes, false));
        var table = reader.ReadUInt32(); var count = reader.ReadUInt32();
        if (count > 100000 || table + count * 16L > bytes.Length) throw new InvalidDataException("Invalid RERL table.");
        var items = new List<Reference>();
        for (var i = 0; i < count; i++)
        {
            reader.BaseStream.Position = table + i * 16;
            var id = reader.ReadUInt64(); var at = reader.BaseStream.Position;
            var offset = reader.ReadInt32(); reader.ReadInt32();
            reader.BaseStream.Position = at + offset;
            var nameBytes = new List<byte>();
            byte value; while ((value = reader.ReadByte()) != 0) nameBytes.Add(value);
            var name = Encoding.UTF8.GetString(nameBytes.ToArray()); var renamed = Rename(name);
            if (renamed != name) changes++;
            references.Add(renamed); items.Add(new(id, renamed));
        }
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(items.Count == 0 ? 0u : 8u); writer.Write(items.Count);
        stream.SetLength(8 + items.Count * 16L); stream.Position = stream.Length;
        for (var i = 0; i < items.Count; i++)
        {
            var start = stream.Position; writer.Write(Encoding.UTF8.GetBytes(items[i].Name)); writer.Write((byte)0); var end = stream.Position;
            stream.Position = 8 + i * 16L; writer.Write(items[i].Id); var offsetBase = stream.Position;
            writer.Write(checked((int)(start - offsetBase))); writer.Write(0); stream.Position = end;
        }
        return stream.ToArray();
    }

    static int RenameTree(KVObject tree, string resource, string block)
    {
        var changed = 0;
        foreach (var pair in tree.Properties.ToArray())
        {
            var value = pair.Value;
            if (value.Value is KVObject nested) changed += RenameTree(nested, resource, block);
            else if (value.Value is string text) { var renamed = Rename(text); if (renamed != text) { tree.Properties[pair.Key] = value is KVFlaggedValue flagged ? new KVFlaggedValue(value.Type, flagged.Flag, renamed) : new KVValue(value.Type, renamed); changed++; } }
            else if (value.Value is byte[] blob && HasOldPath(blob)) throw new InvalidDataException($"Unparsed binary data retains old map paths in {resource}:{block}:{pair.Key}.");
        }
        return changed;
    }

    static byte[] RenameManifest(byte[] bytes, List<string> references, ref int changes)
    {
        // Public ResourceManifest.Read: block offset/count, then per-block
        // offset/count, then an array of signed relative string offsets.
        using var reader = new BinaryReader(new MemoryStream(bytes, false));
        var start = reader.ReadInt32(); var count = reader.ReadInt32();
        if (count < 0 || count > 10000 || start < 8 || start + count * 8L > bytes.Length) throw new InvalidDataException("Invalid resource manifest.");
        var groups = new List<List<string>>();
        for (var i = 0; i < count; i++)
        {
            var at = start + i * 8; reader.BaseStream.Position = at;
            var offset = reader.ReadInt32(); var strings = reader.ReadInt32();
            if (strings < 0 || strings > 100000) throw new InvalidDataException("Invalid manifest string count.");
            var list = new List<string>();
            for (var j = 0; j < strings; j++)
            {
                var reference = at + offset + j * 4; reader.BaseStream.Position = reference;
                var textOffset = reader.ReadInt32(); reader.BaseStream.Position = reference + textOffset;
                var utf8 = new List<byte>(); byte value; while ((value = reader.ReadByte()) != 0) utf8.Add(value);
                var old = Encoding.UTF8.GetString(utf8.ToArray()); var name = Rename(old);
                if (name != old) changes++;
                list.Add(name); references.Add(name);
            }
            groups.Add(list);
        }
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(8); writer.Write(groups.Count); stream.SetLength(8 + groups.Count * 8); stream.Position = stream.Length;
        for (var i = 0; i < groups.Count; i++)
        {
            var table = stream.Position; stream.SetLength(table + groups[i].Count * 4); stream.Position = stream.Length;
            for (var j = 0; j < groups[i].Count; j++)
            {
                var data = stream.Position; writer.Write(Encoding.UTF8.GetBytes(groups[i][j])); writer.Write((byte)0); var end = stream.Position;
                stream.Position = table + j * 4; writer.Write(checked((int)(data - stream.Position))); stream.Position = end;
            }
            var after = stream.Position; stream.Position = 8 + i * 8; writer.Write(checked((int)(table - stream.Position))); writer.Write(groups[i].Count); stream.Position = after;
        }
        return stream.ToArray();
    }

    public static object Build(Package source, string output)
    {
        var variant = Path.Combine(output, "variant");
        var target = Path.Combine(output, NewMapName + ".vpk");
        var temp = Path.Combine(output, NewMapName + ".building.vpk");
        var finalFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var allReferences = new List<string>();
        var changedResources = new List<object>();
        var rawCopyCount = 0;
        foreach (var group in source.Entries)
        foreach (var entry in group.Value)
        {
            var oldPath = entry.DirectoryName + "/" + entry.FileName + "." + entry.TypeName;
            var newPath = Rename(oldPath).Replace('\\', '/');
            var patch = Path.Combine(variant, oldPath.Replace('/', Path.DirectorySeparatorChar));
            byte[] bytes;
            if (File.Exists(patch)) bytes = File.ReadAllBytes(patch); else source.ReadEntry(entry, out bytes, false);
            var original = bytes;
            var stringChanges = 0; var referenceChanges = 0;
            if (oldPath.EndsWith("_c", StringComparison.Ordinal))
            {
                var container = ResourceContainer.Read(bytes);
                var changed = false;
                for (var i = 0; i < container.Blocks.Count; i++)
                {
                    var block = container.Blocks[i];
                    if (block.Type == "RERL")
                    {
                        var before = referenceChanges;
                        var renamed = RenameReferences(block.Bytes, allReferences, ref referenceChanges);
                        if (before != referenceChanges) { container.Blocks[i] = new(block.Type, renamed); changed = true; }
                    }
                    else if (block.Type == "DATA" && oldPath.EndsWith(".vrman_c", StringComparison.Ordinal))
                    {
                        if (container.Blocks.Any(b => b.Type == "NTRO")) throw new InvalidDataException("NTRO resource manifest not supported.");
                        var before = referenceChanges;
                        var renamed = RenameManifest(block.Bytes, allReferences, ref referenceChanges);
                        if (before != referenceChanges) { container.Blocks[i] = new(block.Type, renamed); changed = true; }
                    }
                    else if (block.Bytes.Length >= 4 && (BitConverter.ToUInt32(block.Bytes) & 0xFFFFFF00u) == 0x4B563300u)
                    {
                        if ((BitConverter.ToUInt32(block.Bytes) & 0xFFu) >= 5u)
                        {
                            if (HasOldPath(block.Bytes)) UnrenamedV5.Add(oldPath + ":" + block.Type);
                            continue;
                        }
                        var kv = new BinaryKV3 { Offset = 0, Size = (uint)block.Bytes.Length };
                        using var kvOwner = new Resource();
                        using var reader = new BinaryReader(new MemoryStream(block.Bytes, false)); kv.Read(reader, kvOwner);
                        var count = RenameTree(kv.Data, oldPath, block.Type);
                        if (count > 0)
                        {
                            var rewritten = Kv3V4Writer.Write(kv);
                            var check = new BinaryKV3 { Offset = 0, Size = (uint)rewritten.Length };
                            using var checkReader = new BinaryReader(new MemoryStream(rewritten, false)); check.Read(checkReader, kvOwner);
                            Kv3V4Writer.AssertEquivalent(kv.Data, check.Data);
                            container.Blocks[i] = new(block.Type, rewritten); stringChanges += count; changed = true;
                        }
                    }
                    else if (HasOldPath(block.Bytes)) throw new InvalidDataException($"Unparsed {oldPath}:{block.Type} contains an old map path.");
                }
                if (HasOldPath(container.TrailingBytes)) throw new InvalidDataException($"Streaming data contains an old path: {oldPath}");
                if (changed)
                {
                    bytes = container.Write();
                    var verifyContainer = ResourceContainer.Read(bytes);
                    if (verifyContainer.Blocks.Count != container.Blocks.Count || !verifyContainer.TrailingBytes.SequenceEqual(container.TrailingBytes)) throw new InvalidDataException("Resource streaming bytes changed.");
                }
            }
            else if (oldPath.EndsWith(".kv3", StringComparison.Ordinal))
            {
                if (bytes.Length >= 4 && (BitConverter.ToUInt32(bytes) & 0xFFFFFF00u) == 0x4B563300u && (BitConverter.ToUInt32(bytes) & 0xFFu) == 4u)
                {
                    var kv = new BinaryKV3 { Offset = 0, Size = (uint)bytes.Length };
                    using var owner = new Resource(); using var reader = new BinaryReader(new MemoryStream(bytes, false)); kv.Read(reader, owner);
                    stringChanges = RenameTree(kv.Data, oldPath, "standalone");
                    if (stringChanges > 0) bytes = Kv3V4Writer.Write(kv);
                }
                else if (bytes.Length >= 4 && (BitConverter.ToUInt32(bytes) & 0xFFFFFF00u) == 0x4B563300u)
                {
                    if (HasOldPath(bytes)) UnrenamedV5.Add(oldPath);
                }
                else
                {
                    var text = new UTF8Encoding(false, true).GetString(bytes); var renamed = Rename(text);
                    if (renamed != text) { bytes = Encoding.UTF8.GetBytes(renamed); stringChanges++; }
                }
            }
            else if (HasOldPath(bytes)) throw new InvalidDataException($"Unparsed non-resource {oldPath} contains an old map path.");
            if (ReferenceEquals(bytes, original)) rawCopyCount++;
            else changedResources.Add(new { source = oldPath, target = newPath, stringChanges, referenceChanges, bytes = bytes.Length });
            if (!finalFiles.TryAdd(newPath, bytes)) throw new InvalidDataException("Namespace rename produced a duplicate file.");
        }
        if (!finalFiles.ContainsKey("maps/" + NewMapName + ".vmap_c") || !finalFiles.ContainsKey("maps/" + NewMapName + ".nav")) throw new InvalidDataException("Missing renamed map/nav root.");
        var missingInternalReferences = allReferences.Where(r => r.Replace('\\', '/').StartsWith("maps/" + NewMapName + "/", StringComparison.OrdinalIgnoreCase)).Select(r => r.Replace('\\', '/')).Where(r => !finalFiles.ContainsKey(r) && !finalFiles.ContainsKey(r + "_c")).Distinct().ToArray();
        if (missingInternalReferences.Length != 0) throw new InvalidDataException("Unresolved renamed resources: " + string.Join(",", missingInternalReferences));
        using (var outputPackage = new Package())
        {
            foreach (var file in finalFiles.OrderBy(p => p.Key, StringComparer.Ordinal)) outputPackage.AddFile(file.Key, file.Value);
            outputPackage.Write(temp);
        }
        using (var check = new Package())
        {
            check.Read(temp); check.VerifyHashes(); check.VerifyFileChecksums(null!);
            foreach (var file in finalFiles)
            {
                check.ReadEntry(check.FindEntry(file.Key) ?? throw new InvalidDataException("Missing packed entry."), out byte[] unpacked, false);
                if (!unpacked.SequenceEqual(file.Value)) throw new InvalidDataException("Packed file differs: " + file.Key);
            }
        }
        File.Move(temp, target, true);
        var report = new { status = "independent-map-package-not-engine-tested", mapName = NewMapName, package = target, bytes = new FileInfo(target).Length, files = finalFiles.Count, rawCopyCount, rewrittenResources = changedResources, referenceCount = allReferences.Count, unresolvedInternalReferences = missingInternalReferences, rerLIdsPreserved = true, originalVpkUnmodifiedByTool = true, nativeAcceptance = "Cold-launch this map first; then test original鈫抍lone鈫抩riginal resource cache behavior. Installation and game launch are not performed by this tool." };
        File.WriteAllText(Path.Combine(output, "map-package-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report;
    }
}
