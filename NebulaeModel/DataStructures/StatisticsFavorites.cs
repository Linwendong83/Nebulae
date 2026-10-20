using System;
using System.IO;

namespace NebulaeModel.DataStructures;

/// <summary>Personal statistics favorites, independent of shared production counters.</summary>
internal sealed class StatisticsFavorites
{
    public int ProductionMask;
    public int KillMask;
    public int[] Production = Array.Empty<int>();
    public int[] Kills = Array.Empty<int>();
    private const int MaxEntries = 65536;

    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1);
        writer.Write(ProductionMask & 63);
        writer.Write(KillMask & 7);
        Write(writer, Production, 63);
        Write(writer, Kills, 7);
        return stream.ToArray();
    }

    public static bool TryDecode(byte[] bytes, out StatisticsFavorites favorites)
    {
        favorites = null;
        if (bytes == null || bytes.Length > MaxEntries * 8 + 20) return false;
        try
        {
            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != 1) return false;
            var result = new StatisticsFavorites
            {
                ProductionMask = reader.ReadInt32() & 63,
                KillMask = reader.ReadInt32() & 7,
                Production = Read(reader, 63),
                Kills = Read(reader, 7)
            };
            if (stream.Position != stream.Length) return false;
            favorites = result;
            return true;
        }
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    private static void Write(BinaryWriter writer, int[] values, int mask)
    {
        if (values.Length > MaxEntries) throw new InvalidDataException("Too many statistics favorites");
        writer.Write(values.Length);
        foreach (var value in values) writer.Write(value & mask);
    }

    private static int[] Read(BinaryReader reader, int mask)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > MaxEntries || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 4)
            throw new InvalidDataException("Invalid statistics favorites length");
        var values = new int[count];
        for (var i = 0; i < count; i++) values[i] = reader.ReadInt32() & mask;
        return values;
    }
}
