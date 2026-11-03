/*
 * Binary KV3 v4 layout and serialization adapted for VRF 10.2's legacy
 * KVObject/KVValue API from ValveResourceFormat 15.0, BinaryKV3.Serialization.cs:
 * https://github.com/ValveResourceFormat/ValveResourceFormat/blob/15.0/ValveResourceFormat/Resource/ResourceTypes/BinaryKV3.Serialization.cs
 *
 * The MIT License (MIT)
 * Copyright (c) 2015 ValveResourceFormat Contributors
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using System.Globalization;
using System.Text;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace MapPatchLab;

/// <summary>
/// Writes an uncompressed binary KV3 v4 block, not a Source 2 resource container.
/// Preserves the parsed legacy tree, flags, blob bytes, and floating point bits.
/// VRF 10.2 has already normalized compact integers/floats/typed arrays while
/// reading; their original on-disk encodings cannot be reconstructed from Data.
/// </summary>
public static class Kv3V4Writer
{
    public const uint Magic = 0x4B563304;
    private const uint Trailer = 0xFFEEDD00;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    /// <summary>
    /// Checks every parsed value, array slot, flag and blob. Compact on-disk
    /// scalar types are compared using VRF 10.2's documented normalization;
    /// doubles are bit-exact, including negative zero and NaN payloads.
    /// Object property order and KVObject.Count's potentially stale cache are
    /// not semantic; arrays are compared by their numeric keys.
    /// </summary>
    public static void AssertEquivalent(KVObject expected, KVObject actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        CompareObject(expected, actual, "$", 0);
    }

    private static KVType LogicalType(KVType type) => type switch
    {
        KVType.BOOLEAN_TRUE or KVType.BOOLEAN_FALSE => KVType.BOOLEAN,
        KVType.INT32 or KVType.INT32_AS_BYTE or KVType.INT64_ZERO or KVType.INT64_ONE => KVType.INT64,
        KVType.UINT32 => KVType.UINT64,
        KVType.FLOAT or KVType.DOUBLE_ZERO or KVType.DOUBLE_ONE => KVType.DOUBLE,
        KVType.ARRAY_TYPED or KVType.ARRAY_TYPE_BYTE_LENGTH => KVType.ARRAY,
        _ => type,
    };

    private static void CompareObject(KVObject expected, KVObject actual, string path, int depth)
    {
        if (depth > 512) throw new InvalidDataException($"KV3 comparison nesting exceeds 512 at {path}.");
        if (expected.IsArray != actual.IsArray || expected.Properties.Count != actual.Properties.Count)
            throw new InvalidDataException($"KV3 object/array shape differs at {path}.");
        foreach (var entry in expected.Properties)
        {
            var at = path + "/" + entry.Key;
            if (!actual.Properties.TryGetValue(entry.Key, out var right)) throw new InvalidDataException($"Missing KV3 value at {at}.");
            var left = entry.Value;
            var type = LogicalType(left.Type);
            if (type != LogicalType(right.Type)) throw new InvalidDataException($"KV3 type differs at {at}: {left.Type} versus {right.Type}.");
            if ((left is KVFlaggedValue lf ? lf.Flag : KVFlag.None) != (right is KVFlaggedValue rf ? rf.Flag : KVFlag.None))
                throw new InvalidDataException($"KV3 flag differs at {at}.");
            if (type is KVType.ARRAY or KVType.OBJECT)
            {
                CompareObject((KVObject)left.Value, (KVObject)right.Value, at, depth + 1);
                continue;
            }
            var equal = type switch
            {
                KVType.NULL => left.Value == null && right.Value == null,
                KVType.INT64 or KVType.INT16 => Convert.ToInt64(left.Value, CultureInfo.InvariantCulture) == Convert.ToInt64(right.Value, CultureInfo.InvariantCulture),
                KVType.UINT64 or KVType.UINT16 => Convert.ToUInt64(left.Value, CultureInfo.InvariantCulture) == Convert.ToUInt64(right.Value, CultureInfo.InvariantCulture),
                KVType.DOUBLE => DoubleBits(left) == DoubleBits(right),
                KVType.BINARY_BLOB => ((byte[])left.Value).AsSpan().SequenceEqual((byte[])right.Value),
                KVType.STRING or KVType.BOOLEAN => Equals(left.Value, right.Value),
                _ => throw new NotSupportedException($"Unsupported KV3 comparison type {type} at {at}."),
            };
            if (!equal) throw new InvalidDataException($"KV3 value differs at {at}.");
        }
    }

    private static long DoubleBits(KVValue value) => BitConverter.DoubleToInt64Bits(value.Type == KVType.FLOAT
        ? Convert.ToSingle(value.Value, CultureInfo.InvariantCulture)
        : Convert.ToDouble(value.Value, CultureInfo.InvariantCulture));

    public static byte[] Write(BinaryKV3 parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        return Write(parsed.Data, parsed.Format);
    }

    public static byte[] Write(KVObject data, Guid format)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.IsArray) throw new NotSupportedException("The KV3 block root must be an object.");
        using var context = new Context();
        context.W4.Write(0); // String count is known after walking the tree.
        context.WriteType(KVType.OBJECT, KVFlag.None);
        context.WriteObject(data, false, 0);
        context.Bytes4.Position = 0;
        context.W4.Write(context.Strings.Count);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Magic);
        writer.Write(format.ToByteArray());
        writer.Write(0u); // Compression method: none.
        writer.Write((ushort)0); // Dictionary id.
        writer.Write((ushort)0); // Compression frame size.
        writer.Write(checked((int)context.Bytes1.Length));
        writer.Write(checked((int)context.Bytes4.Length / 4));
        writer.Write(checked((int)context.Bytes8.Length / 8));
        var typesSizePosition = stream.Position;
        writer.Write(0); // Combined string and type section size.
        writer.Write((ushort)0); // Optional preallocation hints, as in upstream.
        writer.Write((ushort)0);
        var dataSizePosition = stream.Position;
        writer.Write(0); // Uncompressed main buffer size.
        writer.Write(0); // Compressed main buffer size (same for method 0).
        writer.Write(context.BlobLengths.Count);
        writer.Write(checked((int)context.Blobs.Length));
        writer.Write(checked((int)context.Bytes2.Length / 2));
        writer.Write(0); // Compressed block size table is absent.

        var dataStart = stream.Position;
        WriteSection(writer, context.Bytes1, dataStart, 1);
        WriteSection(writer, context.Bytes2, dataStart, 2);
        WriteSection(writer, context.Bytes4, dataStart, 4);
        // V4 aligns this section even when it is empty.
        Align(writer, dataStart, 8);
        context.Bytes8.WriteTo(stream);
        var stringsStart = stream.Position;
        foreach (var text in context.Strings)
        {
            writer.Write(Utf8.GetBytes(text));
            writer.Write((byte)0);
        }
        context.Types.WriteTo(stream);
        var typesSize = checked((int)(stream.Position - stringsStart));
        foreach (var length in context.BlobLengths) writer.Write(length);
        writer.Write(Trailer);
        var dataEnd = stream.Position;
        var dataSize = checked((int)(dataEnd - dataStart));
        stream.Position = typesSizePosition;
        writer.Write(typesSize);
        stream.Position = dataSizePosition;
        writer.Write(dataSize);
        writer.Write(dataSize);
        stream.Position = dataEnd;
        if (context.BlobLengths.Count > 0)
        {
            context.Blobs.WriteTo(stream);
            writer.Write(Trailer);
        }
        return stream.ToArray();
    }

    private static void WriteSection(BinaryWriter writer, MemoryStream section, long start, int alignment)
    {
        if (section.Length == 0) return;
        Align(writer, start, alignment);
        section.WriteTo(writer.BaseStream);
    }

    private static void Align(BinaryWriter writer, long start, int alignment)
    {
        while ((writer.BaseStream.Position - start) % alignment != 0) writer.Write((byte)0);
    }

    private sealed class Context : IDisposable
    {
        internal readonly MemoryStream Bytes1 = new(), Bytes2 = new(), Bytes4 = new(), Bytes8 = new(), Types = new(), Blobs = new();
        internal readonly BinaryWriter W1, W2, W4, W8, TypeWriter;
        internal readonly List<int> BlobLengths = new();
        internal readonly List<string> Strings = new();
        private readonly Dictionary<string, int> stringIds = new(StringComparer.Ordinal);
        private readonly HashSet<KVObject> activeObjects = new(ReferenceEqualityComparer.Instance);

        internal Context()
        {
            W1 = new(Bytes1, Utf8, true);
            W2 = new(Bytes2, Utf8, true);
            W4 = new(Bytes4, Utf8, true);
            W8 = new(Bytes8, Utf8, true);
            TypeWriter = new(Types, Utf8, true);
        }

        private int StringId(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length == 0) return -1;
            if (value.Contains('\0')) throw new InvalidDataException("KV3 strings cannot contain embedded NUL characters.");
            if (stringIds.TryGetValue(value, out var id)) return id;
            id = Strings.Count;
            Strings.Add(value);
            stringIds.Add(value, id);
            return id;
        }

        internal void WriteType(KVType type, KVFlag flag)
        {
            if (flag < KVFlag.None || flag > KVFlag.SubClass)
                throw new NotSupportedException($"VRF 10.2 cannot read KV3 flag {flag}.");
            TypeWriter.Write((byte)((byte)type | (flag == KVFlag.None ? 0 : 0x80)));
            if (flag != KVFlag.None) TypeWriter.Write((byte)flag);
        }

        internal void WriteObject(KVObject obj, bool array, int depth)
        {
            if (depth > 512) throw new InvalidDataException("KV3 nesting exceeds 512 levels.");
            if (obj.IsArray != array) throw new InvalidDataException("KV3 value type and object IsArray disagree.");
            if (!activeObjects.Add(obj)) throw new InvalidDataException("KV3 object graph contains a cycle.");
            try
            {
                W4.Write(obj.Properties.Count); // Count may be stale after direct dictionary edits.
                if (array)
                {
                    // KVObject's array contract uses numeric keys; dictionary insertion
                    // order must not change array order after a replacement/edit.
                    for (var i = 0; i < obj.Properties.Count; i++)
                    {
                        if (!obj.Properties.TryGetValue(i.ToString(CultureInfo.InvariantCulture), out var value))
                            throw new InvalidDataException($"KV3 array has a missing element at index {i}.");
                        WriteValue(value, depth + 1);
                    }
                }
                else
                {
                    foreach (var entry in obj.Properties)
                    {
                        W4.Write(StringId(entry.Key));
                        WriteValue(entry.Value, depth + 1);
                    }
                }
            }
            finally { activeObjects.Remove(obj); }
        }

        private void WriteValue(KVValue value, int depth)
        {
            ArgumentNullException.ThrowIfNull(value);
            var flag = value is KVFlaggedValue flagged ? flagged.Flag : KVFlag.None;
            var type = value.Type;
            // Compact array encodings have no retained element-type metadata in
            // legacy KVObject. Their expanded array preserves the parsed values.
            if (type is KVType.ARRAY_TYPED or KVType.ARRAY_TYPE_BYTE_LENGTH) type = KVType.ARRAY;
            WriteType(type, flag);
            var data = value.Value;
            switch (type)
            {
                case KVType.NULL:
                    if (data != null) throw new InvalidDataException("Non-null payload for KV3 NULL.");
                    break;
                case KVType.BOOLEAN: W1.Write((bool)data); break;
                case KVType.BOOLEAN_TRUE:
                case KVType.BOOLEAN_FALSE:
                    if ((bool)data != (type == KVType.BOOLEAN_TRUE)) throw new InvalidDataException("Invalid compact boolean payload.");
                    break;
                case KVType.INT16: W2.Write(Convert.ToInt16(data, CultureInfo.InvariantCulture)); break;
                case KVType.UINT16: W2.Write(Convert.ToUInt16(data, CultureInfo.InvariantCulture)); break;
                case KVType.INT32: W4.Write(Convert.ToInt32(data, CultureInfo.InvariantCulture)); break;
                case KVType.UINT32: W4.Write(Convert.ToUInt32(data, CultureInfo.InvariantCulture)); break;
                case KVType.INT32_AS_BYTE: W1.Write(Convert.ToByte(data, CultureInfo.InvariantCulture)); break;
                case KVType.INT64: W8.Write(Convert.ToInt64(data, CultureInfo.InvariantCulture)); break;
                case KVType.UINT64: W8.Write(Convert.ToUInt64(data, CultureInfo.InvariantCulture)); break;
                case KVType.DOUBLE: W8.Write(Convert.ToDouble(data, CultureInfo.InvariantCulture)); break;
                case KVType.FLOAT: W4.Write(Convert.ToSingle(data, CultureInfo.InvariantCulture)); break;
                case KVType.INT64_ZERO:
                case KVType.INT64_ONE:
                    if (Convert.ToInt64(data, CultureInfo.InvariantCulture) != (type == KVType.INT64_ONE ? 1L : 0L))
                        throw new InvalidDataException("Invalid compact integer payload.");
                    break;
                case KVType.DOUBLE_ZERO:
                case KVType.DOUBLE_ONE:
                    if (BitConverter.DoubleToInt64Bits(Convert.ToDouble(data, CultureInfo.InvariantCulture)) !=
                        BitConverter.DoubleToInt64Bits(type == KVType.DOUBLE_ONE ? 1d : 0d))
                        throw new InvalidDataException("Invalid compact floating point payload.");
                    break;
                case KVType.STRING: W4.Write(StringId((string)data)); break;
                case KVType.BINARY_BLOB:
                    var bytes = (byte[])data;
                    BlobLengths.Add(bytes.Length);
                    Blobs.Write(bytes);
                    break;
                case KVType.OBJECT: WriteObject((KVObject)data, false, depth); break;
                case KVType.ARRAY: WriteObject((KVObject)data, true, depth); break;
                default: throw new NotSupportedException($"Unsupported binary KV3 type {type}; refusing to lose data.");
            }
        }

        public void Dispose()
        {
            W1.Dispose(); W2.Dispose(); W4.Dispose(); W8.Dispose(); TypeWriter.Dispose();
            Bytes1.Dispose(); Bytes2.Dispose(); Bytes4.Dispose(); Bytes8.Dispose(); Types.Dispose(); Blobs.Dispose();
        }
    }
}
