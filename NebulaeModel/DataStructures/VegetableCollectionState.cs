using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace NebulaeModel.DataStructures;

internal static class VegetableCollectionState
{
    public const int MaxBytes = 8 * 1024 * 1024;

    // Native Export enumerates dictionaries and linked lists in allocation order. Import
    // inserts owner nodes at the head, so even an unchanged round trip can reorder them.
    internal static Dictionary<(long Owner, int Proto), int> Counts(byte[] data)
    {
        var result = new Dictionary<(long, int), int>();
        if (data == null || data.Length > MaxBytes) throw new InvalidDataException("Invalid vegetation snapshot.");
        if (data.Length == 0) return result;
        using var stream = new MemoryStream(data, false);
        using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 0) throw new InvalidDataException("Unknown vegetation format.");
        int ReadCount(int stride)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > (stream.Length - stream.Position) / stride)
                throw new InvalidDataException("Invalid vegetation count.");
            return count;
        }
        void ReadNode(long owner)
        {
            var proto = reader.ReadInt32(); var count = reader.ReadInt32();
            if (proto <= 0 || proto >= 9999 || count < 0) throw new InvalidDataException("Invalid vegetation node.");
            if (count == 0) return;
            var key = (owner, proto);
            result.TryGetValue(key, out var previous);
            result[key] = checked(previous + count);
        }
        var players = ReadCount(8);
        for (var i = 0; i < players; i++) ReadNode(-1);
        var owners = ReadCount(12);
        for (var i = 0; i < owners; i++)
        {
            var owner = reader.ReadInt64();
            var nodes = ReadCount(8);
            for (var j = 0; j < nodes; j++) ReadNode(owner);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected vegetation tail.");
        return result;
    }

    internal static byte[] FromCounts(Dictionary<(long Owner, int Proto), int> counts)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var players = counts.Where(x => x.Key.Owner == -1 && x.Value > 0).OrderBy(x => x.Key.Proto).ToArray();
        var owners = counts.Where(x => x.Key.Owner != -1 && x.Value > 0).GroupBy(x => x.Key.Owner).OrderBy(x => x.Key).ToArray();
        writer.Write(0); writer.Write(players.Length);
        foreach (var item in players) { writer.Write(item.Key.Proto); writer.Write(item.Value); }
        writer.Write(owners.Length);
        foreach (var owner in owners)
        {
            writer.Write(owner.Key); writer.Write(owner.Count());
            foreach (var item in owner.OrderBy(x => x.Key.Proto)) { writer.Write(item.Key.Proto); writer.Write(item.Value); }
        }
        if (stream.Length > MaxBytes) throw new InvalidDataException("Vegetation collection is too large.");
        return stream.ToArray();
    }

    public static bool Equivalent(byte[] left, byte[] right)
    {
        try
        {
            var a = Counts(left); var b = Counts(right);
            return a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var value) && value == x.Value);
        }
        catch (Exception error) when (error is IOException || error is OverflowException) { return false; }
    }

    public static byte[] Capture(VegetableCollection collection)
    {
        if (collection == null) return Array.Empty<byte>();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            collection.Export(writer);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Vegetation collection is too large");
        return stream.ToArray();
    }

    public static void Restore(VegetableCollection collection, byte[] data)
    {
        if (collection == null || data == null || data.Length == 0) return;
        if (data.Length > MaxBytes) throw new InvalidDataException("Vegetation collection is too large");
        using var stream = new MemoryStream(data, false);
        using var reader = new BinaryReader(stream);
        NebulaeModel.Networking.BinaryUtils.RequireHeader(reader, 0);
        collection.Import(reader);
        if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected vegetation collection tail");
    }
}
