using System;
using System.IO;
using System.Text;

namespace NebulaeModel.Packets.Authority;

/// <summary>Thread-local native IO scratch; only the immutable encoded result escapes.</summary>
internal static class NativeComponentCodec<T> where T : struct, IPoolElement
{
    private static readonly byte header = CurrentHeader();
    [ThreadStatic] private static MemoryStream output;
    [ThreadStatic] private static BinaryWriter writer;
    [ThreadStatic] private static BufferStream input;
    [ThreadStatic] private static BinaryReader reader;
    [ThreadStatic] private static bool writing, reading;

    private static byte CurrentHeader()
    {
        using var stream = new MemoryStream();
        using var binary = new BinaryWriter(stream);
        default(T).Export(binary);
        return stream.GetBuffer()[0];
    }

    public static byte[] Encode(in T value)
    {
        if (writing)
        {
            using var nested = new MemoryStream(); using var binary = new BinaryWriter(nested);
            value.Export(binary); return nested.ToArray();
        }
        writing = true;
        try
        {
            if (output == null) { output = new MemoryStream(256); writer = new BinaryWriter(output, Encoding.UTF8, true); }
            output.Position = 0; output.SetLength(0);
            value.Export(writer); writer.Flush();
            return output.ToArray();
        }
        finally { writing = false; }
    }

    public static bool TryDecode(byte[] bytes, out T value)
    {
        value = default;
        if (bytes == null || bytes.Length == 0 || bytes[0] != header) return false;
        if (reading)
        {
            using var nested = new MemoryStream(bytes, false); using var binary = new BinaryReader(nested);
            value.Import(binary); return nested.Position == nested.Length;
        }
        reading = true;
        try
        {
            if (input == null) { input = new BufferStream(); reader = new BinaryReader(input, Encoding.UTF8, true); }
            input.Open(bytes); value.Import(reader); return input.Position == input.Length;
        }
        finally { input?.Release(); reading = false; }
    }

    private sealed class BufferStream : Stream
    {
        private byte[] buffer;
        private int position;
        public void Open(byte[] bytes) { buffer = bytes; position = 0; }
        public void Release() { buffer = null; position = 0; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => buffer?.Length ?? 0;
        public override long Position
        {
            get => position;
            set { if (value < 0 || value > Length) throw new IOException("Native buffer position is out of range."); position = (int)value; }
        }
        public override int ReadByte() => position < Length ? buffer[position++] : -1;
        public override int Read(byte[] destination, int offset, int count)
        {
            var available = Math.Min(count, (int)Length - position);
            if (available > 0) Buffer.BlockCopy(buffer, position, destination, offset, available);
            position += available; return available;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length) + offset;
            return Position;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] bytes, int offset, int count) => throw new NotSupportedException();
    }
}
