using System.Text;

namespace MapPatchLab;

// Format-level laboratory writer. Does not load Valve DLLs or modify a mounted map.
// Unknown blocks are retained byte-for-byte; their table indices never change.
public sealed class ResourceContainer
{
    public sealed record Block(string Type, byte[] Bytes);
    public ushort HeaderVersion { get; private init; }
    public ushort ResourceVersion { get; private init; }
    public byte[] TrailingBytes { get; private init; } = Array.Empty<byte>();
    public List<Block> Blocks { get; } = new();

    public static ResourceContainer Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        var bodySize = reader.ReadUInt32();
        if (bodySize < 16 || bodySize > bytes.Length) throw new InvalidDataException("Truncated resource bytes.");
        var resource = new ResourceContainer { HeaderVersion = reader.ReadUInt16(), ResourceVersion = reader.ReadUInt16(), TrailingBytes = bytes.AsSpan((int)bodySize).ToArray() };
        if (resource.HeaderVersion != 12) throw new InvalidDataException("Only resource header v12 has been verified.");
        var tableStart = 8L + reader.ReadUInt32();
        var count = reader.ReadUInt32();
        if (count > 4096 || tableStart < 16 || tableStart + count * 12 > bodySize) throw new InvalidDataException("Invalid resource block table.");
        for (var i = 0; i < count; i++)
        {
            stream.Position = tableStart + i * 12;
            var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var position = stream.Position;
            var offset = position + reader.ReadUInt32();
            var size = reader.ReadUInt32();
            if (offset < tableStart + count * 12 || offset + size > bodySize) throw new InvalidDataException("Invalid resource block bounds.");
            resource.Blocks.Add(new(type, bytes.AsSpan(checked((int)offset), checked((int)size)).ToArray()));
        }
        return resource;
    }

    public byte[] Write()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0u);
        writer.Write(HeaderVersion);
        writer.Write(ResourceVersion);
        writer.Write(8u);
        writer.Write((uint)Blocks.Count);
        stream.SetLength(16 + 12 * Blocks.Count);
        stream.Position = stream.Length;
        for (var i = 0; i < Blocks.Count; i++)
        {
            while (stream.Position % 16 != 0) writer.Write((byte)0);
            var start = stream.Position;
            writer.Write(Blocks[i].Bytes);
            var end = stream.Position;
            stream.Position = 16 + 12 * i;
            writer.Write(Encoding.ASCII.GetBytes(Blocks[i].Type));
            var offsetBase = stream.Position;
            writer.Write(checked((uint)(start - offsetBase)));
            writer.Write(checked((uint)Blocks[i].Bytes.Length));
            stream.Position = end;
        }
        var bodyEnd = stream.Length;
        stream.Position = 0;
        writer.Write(checked((uint)bodyEnd));
        stream.Position = bodyEnd;
        writer.Write(TrailingBytes);
        return stream.ToArray();
    }
}
