using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace NebulaeModel.Networking;

/// <summary>A serialized field, or a primitive array with a fixed-width length prefix.</summary>
internal readonly struct WireField
{
    public WireField(int offset, int length, int elementSize = 0, int prefix = 0)
    { Offset = offset; Length = length; ElementSize = elementSize; Prefix = prefix; }
    public int Offset { get; }
    public int Length { get; }
    public int ElementSize { get; }
    public int Prefix { get; }
}

/// <summary>Absolute field replacements. Sparse arrays carry element indexes, never arithmetic deltas.</summary>
internal static class FieldDeltaCodec
{
    public const int MaxFields = 65536;
    public const int MaxBytes = PacketCompression.MaxDecompressedMessageBytes;
    [ThreadStatic] private static EncodingScratch scratch;

    private sealed class EncodingScratch
    {
        public byte[] Buffer = new byte[256];
        public List<int> Indexes = new();
        public bool InUse;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Equal(byte[] a, int ao, byte[] b, int bo, int count)
    {
        for (var i = 0; i < count; i++) if (a[ao + i] != b[bo + i]) return false;
        return true;
    }

    public static bool ValidLayout(byte[] bytes, IReadOnlyList<WireField> fields)
    {
        if (bytes == null || fields == null || fields.Count > MaxFields || bytes.Length > MaxBytes) return false;
        var end = 0;
        if (fields is WireField[] array)
        {
            for (var i = 0; i < array.Length; i++) if (!ValidField(bytes, array[i], ref end)) return false;
            return end == bytes.Length;
        }
        for (var i = 0; i < fields.Count; i++)
        {
            if (!ValidField(bytes, fields[i], ref end)) return false;
        }
        return end == bytes.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ValidField(byte[] bytes, in WireField field, ref int end)
    {
        if (field.Offset != end || field.Length < 0 || field.Length > bytes.Length - end ||
            field.ElementSize < 0 || field.Prefix < 0 || field.Prefix > field.Length ||
            field.ElementSize > 0 && (field.Length - field.Prefix) % field.ElementSize != 0) return false;
        end += field.Length; return true;
    }

    private static WireField[] AsArray(IReadOnlyList<WireField> fields)
    {
        if (fields is WireField[] array) return array;
        var copy = new WireField[fields.Count];
        for (var i = 0; i < copy.Length; i++) copy[i] = fields[i];
        return copy;
    }

    public static bool TryEncode(byte[] before, IReadOnlyList<WireField> oldFields,
        byte[] after, IReadOnlyList<WireField> newFields, out byte[] delta) =>
        TryEncode(before, oldFields, after, newFields, MaxBytes, out delta);

    public static bool TryEncode(byte[] before, IReadOnlyList<WireField> oldFields,
        byte[] after, IReadOnlyList<WireField> newFields, int maxEncodedBytes, out byte[] delta)
    {
        delta = null;
        if (!ValidLayout(before, oldFields) || !ValidLayout(after, newFields) || oldFields.Count != newFields.Count) return false;
        return TryEncodeArrays(before, AsArray(oldFields), after, AsArray(newFields), maxEncodedBytes, out delta);
    }

    private static bool TryEncodeArrays(byte[] before, WireField[] oldFields, byte[] after, WireField[] newFields,
        int maxEncodedBytes, out byte[] delta)
    {
        delta = null;
        var size = CountBytes(newFields.Length) + (newFields.Length + 7) / 8;
        var limit = Math.Min(maxEncodedBytes, MaxBytes);
        if (size > limit) return false;
        var work = scratch;
        if (work == null || work.InUse)
        {
            work = new EncodingScratch();
            if (scratch?.InUse != true) scratch = work;
        }
        work.InUse = true;
        work.Indexes.Clear();
        try
        {
            EnsureCapacity(work, size, limit);
            var position = 0;
            WriteCount(work.Buffer, ref position, newFields.Length);
            var maskOffset = position; var maskBytes = (newFields.Length + 7) / 8;
            Array.Clear(work.Buffer, maskOffset, maskBytes); position += maskBytes;
            // Check each replacement's cost before writing it. The only owned patch allocation is
            // the final exact-sized copy, and the field table is traversed once on Mono as well.
            for (var i = 0; i < newFields.Length; i++)
            {
                var a = oldFields[i]; var b = newFields[i];
                if (a.Length == b.Length && Equal(before, a.Offset, after, b.Offset, a.Length)) continue;
                var bytes = b.Length; byte mode = 0;
                if (a.ElementSize != 0 || b.ElementSize != 0 || a.Length != b.Length)
                {
                    mode = 1;
                    bytes += CountBytes(b.Length);
                    if (a.ElementSize > 0 && a.ElementSize == b.ElementSize && a.Prefix == b.Prefix &&
                        a.Length == b.Length && Equal(before, a.Offset, after, b.Offset, a.Prefix))
                    {
                        work.Indexes.Clear();
                        var sparseBytes = MeasureSparse(before, a.Offset + a.Prefix, after, b.Offset + b.Prefix,
                            (b.Length - b.Prefix) / b.ElementSize, b.ElementSize, bytes, work.Indexes);
                        if (sparseBytes < bytes)
                        { mode = 2; bytes = sparseBytes; }
                    }
                }
                if (bytes >= limit - position) return false;
                EnsureCapacity(work, position + 1 + bytes, limit);
                var output = work.Buffer;
                output[maskOffset + i / 8] |= (byte)(1 << (i % 8)); output[position++] = mode;
                if (mode == 2)
                {
                    WriteCount(output, ref position, work.Indexes.Count);
                    for (var j = 0; j < work.Indexes.Count; j++)
                    {
                        var index = work.Indexes[j];
                        var offset = b.Offset + b.Prefix + index * b.ElementSize;
                        WriteCount(output, ref position, index);
                        Buffer.BlockCopy(after, offset, output, position, b.ElementSize); position += b.ElementSize;
                    }
                }
                else
                {
                    if (mode == 1) WriteCount(output, ref position, b.Length);
                    Buffer.BlockCopy(after, b.Offset, output, position, b.Length); position += b.Length;
                }
            }
            var result = new byte[position]; Buffer.BlockCopy(work.Buffer, 0, result, 0, position);
            delta = result; return true;
        }
        finally
        {
            work.InUse = false; work.Indexes.Clear();
            if (work.Indexes.Capacity > MaxFields) work.Indexes = new List<int>();
            if (work.Buffer.Length > 65536) work.Buffer = new byte[256];
        }
    }

    private static void EnsureCapacity(EncodingScratch work, int required, int limit)
    {
        if (required <= work.Buffer.Length) return;
        Array.Resize(ref work.Buffer, Math.Min(limit, Math.Max(required, work.Buffer.Length * 2)));
    }

    private static int MeasureSparse(byte[] before, int oldOffset, byte[] after, int newOffset,
        int count, int width, int ceiling, List<int> indexes)
    {
        var changed = 0; var bytes = 0;
        for (var index = 0; index < count; index++)
        {
            if (width == 1 ? before[oldOffset + index] == after[newOffset + index] :
                Equal(before, oldOffset + index * width, after, newOffset + index * width, width)) continue;
            changed++; bytes += CountBytes(index) + width; indexes.Add(index);
            if (bytes + CountBytes(changed) >= ceiling) return ceiling;
        }
        return bytes + CountBytes(changed);
    }

    public static byte[] Apply(byte[] before, IReadOnlyList<WireField> fields, byte[] delta, int maxBytes = MaxBytes)
    {
        if (!ValidLayout(before, fields) || delta == null || delta.Length > maxBytes) throw new InvalidDataException("Invalid field baseline or patch.");
        var layout = AsArray(fields);
        var position = 0;
        if (ReadCount(delta, ref position, MaxFields) != layout.Length) throw new InvalidDataException("Field count differs from baseline.");
        var maskOffset = position;
        Skip(delta, ref position, (layout.Length + 7) / 8);
        if (layout.Length % 8 != 0 && (delta[position - 1] >> (layout.Length % 8)) != 0)
            throw new InvalidDataException("Unknown field bits.");
        var payloadOffset = position;
        var size = 0;
        // Validate every replacement and calculate the final size before allocating or copying.
        for (var i = 0; i < layout.Length; i++)
        {
            var field = layout[i]; var length = field.Length;
            if ((delta[maskOffset + i / 8] & (1 << (i % 8))) != 0)
            {
                Skip(delta, ref position, 1); var mode = delta[position - 1];
                if (mode == 0)
                {
                    if (field.ElementSize != 0) throw new InvalidDataException("Array encoded as scalar.");
                    Skip(delta, ref position, length);
                }
                else if (mode == 1) { length = ReadCount(delta, ref position, maxBytes); Skip(delta, ref position, length); }
                else if (mode == 2)
                {
                    if (field.ElementSize <= 0) throw new InvalidDataException("Sparse scalar field.");
                    var elements = (field.Length - field.Prefix) / field.ElementSize;
                    var count = ReadCount(delta, ref position, elements); var previous = -1;
                    for (var j = 0; j < count; j++)
                    {
                        var index = ReadCount(delta, ref position, elements - 1);
                        if (index <= previous) throw new InvalidDataException("Unordered sparse elements.");
                        Skip(delta, ref position, field.ElementSize); previous = index;
                    }
                }
                else throw new InvalidDataException("Unknown field encoding.");
            }
            EnsureSize(size, length, maxBytes); size += length;
        }
        if (position != delta.Length) throw new InvalidDataException("Trailing field patch bytes.");
        var result = new byte[size]; var destination = 0; position = payloadOffset;
        for (var i = 0; i < layout.Length; i++)
        {
            var field = layout[i]; var length = field.Length;
            if ((delta[maskOffset + i / 8] & (1 << (i % 8))) == 0)
                Buffer.BlockCopy(before, field.Offset, result, destination, length);
            else
            {
                var mode = delta[position++];
                if (mode == 2)
                {
                    Buffer.BlockCopy(before, field.Offset, result, destination, length);
                    var elements = (field.Length - field.Prefix) / field.ElementSize;
                    var count = ReadCount(delta, ref position, elements);
                    for (var j = 0; j < count; j++)
                    {
                        var index = ReadCount(delta, ref position, elements - 1);
                        Buffer.BlockCopy(delta, position, result, destination + field.Prefix + index * field.ElementSize, field.ElementSize);
                        position += field.ElementSize;
                    }
                }
                else
                {
                    if (mode == 1) length = ReadCount(delta, ref position, maxBytes);
                    Buffer.BlockCopy(delta, position, result, destination, length); position += length;
                }
            }
            destination += length;
        }
        return result;
    }

    private static void Skip(byte[] data, ref int position, int count)
    { if (count < 0 || count > data.Length - position) throw new InvalidDataException("Truncated state."); position += count; }

    private static void WriteCount(byte[] data, ref int position, int value)
    { var bits = (uint)value; while (bits >= 128) { data[position++] = (byte)(bits | 128); bits >>= 7; } data[position++] = (byte)bits; }

    private static int ReadCount(byte[] data, ref int position, int max)
    {
        uint value = 0;
        for (var shift = 0; shift <= 28; shift += 7)
        {
            Skip(data, ref position, 1); var next = data[position - 1];
            if (shift == 28 && (next & 0xf8) != 0) throw new InvalidDataException("State count overflow.");
            value |= (uint)(next & 127) << shift;
            if ((next & 128) != 0) continue;
            if (value > max) throw new InvalidDataException("State count exceeds its ceiling.");
            return (int)value;
        }
        throw new InvalidDataException("Invalid state count.");
    }

    private static void EnsureSize(long size, int additional, int max)
    { if (additional < 0 || size + additional > max) throw new InvalidDataException("Expanded state exceeds its ceiling."); }

    internal static byte[] ReadExact(BinaryReader reader, int length)
    {
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Truncated state.");
        var result = reader.ReadBytes(length);
        if (result.Length != length) throw new EndOfStreamException();
        return result;
    }

    internal static void WriteCount(BinaryWriter writer, int value)
    { var bits = (uint)value; while (bits >= 128) { writer.Write((byte)(bits | 128)); bits >>= 7; } writer.Write((byte)bits); }
    internal static int ReadCount(BinaryReader reader, int max)
    {
        uint value = 0;
        for (var shift = 0; shift <= 28; shift += 7)
        {
            var next = reader.ReadByte();
            if (shift == 28 && (next & 0xf8) != 0) throw new InvalidDataException("State count overflow.");
            value |= (uint)(next & 127) << shift;
            if ((next & 128) != 0) continue;
            if (value > max) throw new InvalidDataException("State count exceeds its ceiling.");
            return (int)value;
        }
        throw new InvalidDataException("Invalid state count.");
    }
    private static int CountBytes(int value) { var count = 1; while ((value >>= 7) != 0) count++; return count; }
}
