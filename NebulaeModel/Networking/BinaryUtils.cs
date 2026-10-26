using K4os.Compression.LZ4.Streams;
using System;
using System.IO;
using NebulaeModel.Networking.Serialization;

namespace NebulaeModel.Networking
{
    internal static class BinaryUtils
    {
        private const int BUFFER_SIZE = 8192;

        internal static void RequireHeader(BinaryReader reader, int expected)
        {
            var position = reader.BaseStream.Position;
            if (reader.ReadInt32() != expected) throw new ParseException("Invalid binary payload header");
            reader.BaseStream.Position = position;
        }

        internal class Writer : System.IDisposable
        {
            private readonly MemoryStream ms;
            private readonly LZ4EncoderStream ls;
            private readonly BufferedStream bs;
            private readonly BinaryWriter bw;

            public BinaryWriter BinaryWriter => bw;

            public Writer()
            {
                ms = new MemoryStream();
                ls = LZ4Stream.Encode(ms);
                bs = new BufferedStream(ls, BUFFER_SIZE);
                bw = new BinaryWriter(bs);
            }

            public void Dispose()
            {
                bw?.Close();
                bs?.Dispose();
                ls?.Dispose();
                ms?.Dispose();
                GC.SuppressFinalize(this);
            }

            public byte[] CloseAndGetBytes()
            {
                bw?.Close();
                return ms?.ToArray() ?? Array.Empty<byte>();
            }
        }

        internal class Reader : System.IDisposable
        {
            private readonly MemoryStream ms;
            private readonly LZ4DecoderStream ls;
            private readonly BufferedStream bs;
            private readonly BinaryReader br;

            public BinaryReader BinaryReader => br;

            public Reader(byte[] bytes, int? header = null, bool byteHeader = false)
            {
                ms = new MemoryStream(bytes);
                ls = LZ4Stream.Decode(ms);
                bs = new BufferedStream(ls, BUFFER_SIZE);
                br = header.HasValue ? new HeaderReader(bs, header.Value, byteHeader) : new BinaryReader(bs);
            }

            public void RequireEnd()
            {
                if (br.BaseStream.ReadByte() != -1) throw new ParseException("Trailing binary payload data");
            }

            public void Dispose()
            {
                br?.Close();
                bs?.Dispose();
                ls?.Dispose();
                ms?.Dispose();
                GC.SuppressFinalize(this);
            }
        }

        private sealed class HeaderReader(Stream stream, int header, bool byteHeader) : BinaryReader(stream)
        {
            private bool headerRead;

            public override int ReadInt32()
            {
                var value = base.ReadInt32();
                if (!byteHeader && !headerRead)
                {
                    if (value != header) throw new ParseException("Invalid binary payload header");
                    headerRead = true;
                }
                return value;
            }

            public override byte ReadByte()
            {
                var value = base.ReadByte();
                if (byteHeader && !headerRead)
                {
                    if (value != header) throw new ParseException("Invalid binary payload header");
                    headerRead = true;
                }
                return value;
            }
        }
    }
}
