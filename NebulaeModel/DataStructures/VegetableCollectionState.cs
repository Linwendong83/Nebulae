using System;
using System.IO;

namespace NebulaeModel.DataStructures;

internal static class VegetableCollectionState
{
    public const int MaxBytes = 8 * 1024 * 1024;

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
        collection.Import(reader);
        if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected vegetation collection tail");
    }
}
