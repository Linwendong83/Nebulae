using System.IO;

namespace NebulaeModel.Packets.Factory;

internal readonly struct ManualBuildIntent
{
    public const byte Category = 5;
    public ManualBuildIntent(int planet, int prebuild, long generation, int count, int inc)
    { Planet = planet; Prebuild = prebuild; Generation = generation; Count = count; Inc = inc; }
    public int Planet { get; }
    public int Prebuild { get; }
    public long Generation { get; }
    public int Count { get; }
    public int Inc { get; }
    public byte[] Encode()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Planet); writer.Write(Prebuild); writer.Write(Generation); writer.Write(Count); writer.Write(Inc);
        return stream.ToArray();
    }
    public static bool TryDecode(byte[] data, out ManualBuildIntent intent)
    {
        intent = default;
        if (data?.Length != 24) return false;
        using var reader = new BinaryReader(new MemoryStream(data, false));
        intent = new ManualBuildIntent(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt32());
        return intent.Planet > 0 && intent.Prebuild > 0 && intent.Generation > 0 && intent.Count >= 0 &&
               intent.Count <= 10000 && intent.Inc >= 0 && (long)intent.Inc <= (long)intent.Count * 10;
    }
}
