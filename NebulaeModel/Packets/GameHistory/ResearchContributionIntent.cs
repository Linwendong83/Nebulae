using System.IO;

namespace NebulaeModel.Packets.GameHistory;

internal readonly struct ResearchContributionIntent
{
    public const byte Category = 4;
    public const long MaxHashes = 1_000_000;
    public ResearchContributionIntent(int tech, int level, long hashes) { Tech = tech; Level = level; Hashes = hashes; }
    public int Tech { get; }
    public int Level { get; }
    public long Hashes { get; }
    public byte[] Encode()
    {
        using var stream = new MemoryStream(16); using var writer = new BinaryWriter(stream);
        writer.Write(Tech); writer.Write(Level); writer.Write(Hashes); return stream.ToArray();
    }
    public static bool TryDecode(byte[] bytes, out ResearchContributionIntent intent)
    {
        intent = default;
        if (bytes?.Length != 16) return false;
        using var reader = new BinaryReader(new MemoryStream(bytes));
        intent = new ResearchContributionIntent(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt64());
        return intent.Tech > 0 && intent.Level >= 0 && intent.Hashes > 0 && intent.Hashes <= MaxHashes;
    }
}
