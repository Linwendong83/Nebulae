using System.Collections.Generic;
using System.IO;

namespace NebulaeModel.Utils;

/// <summary>Append-before-effect log. A torn final append is uncommitted and can be retried by its sender.</summary>
internal static class BinaryRecordLog
{
    private const int MaximumRecordSize = 8 * 1024 * 1024;

    public static List<byte[]> Read(string path)
    {
        var records = new List<byte[]>();
        if (!File.Exists(path)) return records;
        long validLength = 0;
        long length;
        using (var stream = File.OpenRead(path))
        using (var reader = new BinaryReader(stream))
        {
            length = stream.Length;
            while (stream.Position < length)
            {
                if (length - stream.Position < 4) break;
                var size = reader.ReadInt32();
                if (size <= 0 || size > MaximumRecordSize) throw new InvalidDataException("Invalid durable record length");
                if (length - stream.Position < size) break;
                records.Add(reader.ReadBytes(size));
                validLength = stream.Position;
            }
        }
        if (validLength != length)
        {
            File.Copy(path, path + ".interrupted", true);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.SetLength(validLength);
            stream.Flush(true);
        }
        return records;
    }

    public static void Append(string path, byte[] bytes)
    {
        if (bytes.Length <= 0 || bytes.Length > MaximumRecordSize) throw new InvalidDataException("Invalid durable record size");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        writer.Flush();
        stream.Flush(true);
    }
}
