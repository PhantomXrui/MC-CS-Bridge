using System.Text;
using ValveResourceFormat.Blocks;

namespace MapPatchLab;

public static class MbufWriter
{
    // Mirrors the published VRF VBIB.Read layout. Data is deliberately uncompressed.
    // The original size/flags word is retained. This sample is parsed by VRF10.2,
    // which only masks bit31: adding later-format bit26 would corrupt its stride.
    public static byte[] Write(VBIB buffers, byte[] originalBlock)
    {
        using var old = new BinaryReader(new MemoryStream(originalBlock, false));
        var vertexOffset = old.ReadUInt32();
        var vertexCount = old.ReadUInt32();
        var indexOffset = old.ReadUInt32();
        var indexCount = old.ReadUInt32();
        if (vertexCount != buffers.VertexBuffers.Count || indexCount != buffers.IndexBuffers.Count) throw new InvalidDataException("Buffer count changed.");
        var all = buffers.VertexBuffers.Concat(buffers.IndexBuffers).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(16u);
        writer.Write(vertexCount);
        writer.Write(checked((uint)(8 + vertexCount * 24)));
        writer.Write(indexCount);
        stream.SetLength(16 + all.Length * 24);
        stream.Position = stream.Length;
        for (var i = 0; i < all.Length; i++)
        {
            var buffer = all[i];
            if (buffer.Data.Length != checked(buffer.ElementCount * buffer.ElementSizeInBytes)) throw new InvalidDataException("Decoded data size mismatch.");
            var oldRecord = i < vertexCount ? vertexOffset + i * 24L : 8L + indexOffset + (i - vertexCount) * 24L;
            old.BaseStream.Position = oldRecord + 4;
            var oldFlags = old.ReadUInt32();
            var record = 16 + i * 24L;
            var attributes = stream.Position;
            foreach (var field in buffer.InputLayoutFields)
            {
                var name = Encoding.ASCII.GetBytes(field.SemanticName);
                if (name.Length >= 32) throw new InvalidDataException("Attribute semantic too long.");
                writer.Write(name);
                writer.Write(new byte[32 - name.Length]);
                writer.Write(field.SemanticIndex);
                writer.Write((uint)field.Format);
                writer.Write(field.Offset);
                writer.Write(field.Slot);
                writer.Write((uint)field.SlotType);
                writer.Write(field.InstanceStepRate);
            }
            while (stream.Position % 16 != 0) writer.Write((byte)0);
            var data = stream.Position;
            writer.Write(buffer.Data);
            var end = stream.Position;
            stream.Position = record;
            writer.Write(buffer.ElementCount);
            if ((oldFlags & 0x7FFFFFFFu) != buffer.ElementSizeInBytes) throw new InvalidDataException("Only the inspected 10.2 buffer flag layout is supported.");
            writer.Write(oldFlags);
            writer.Write(checked((uint)(attributes - record - 8)));
            writer.Write((uint)buffer.InputLayoutFields.Length);
            writer.Write(checked((uint)(data - record - 16)));
            writer.Write(buffer.Data.Length);
            stream.Position = end;
        }
        return stream.ToArray();
    }
}
