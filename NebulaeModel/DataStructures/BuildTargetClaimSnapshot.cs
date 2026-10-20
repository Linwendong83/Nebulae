using System.Collections.Generic;
using System.IO;

namespace NebulaeModel.DataStructures;

internal static class BuildTargetClaimSnapshot
{
    public static byte[] Export(IEnumerable<BuildTargetClaim> claims, int planetId)
    {
        var selected = new List<BuildTargetClaim>();
        foreach (var claim in claims)
            if (claim.PlanetId == planetId) selected.Add(claim);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(selected.Count);
        foreach (var claim in selected)
        {
            writer.Write(claim.PrebuildId);
            writer.Write(claim.Generation);
            writer.Write((byte)claim.OwnerKind);
            writer.Write(claim.OwnerId);
            writer.Write(claim.Launched);
        }
        return stream.ToArray();
    }

    public static List<BuildTargetClaim> Import(byte[] data, int planetId, int maximumCount)
    {
        if (data == null) throw new InvalidDataException("Missing build assignment snapshot");
        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream);
        var count = reader.ReadInt32();
        if (count < 0 || count > maximumCount) throw new InvalidDataException("Invalid build assignment count");
        var result = new List<BuildTargetClaim>(count);
        var seen = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var id = reader.ReadInt32();
            var generation = reader.ReadInt64();
            var kind = (BuildOwnerKind)reader.ReadByte();
            var ownerId = reader.ReadInt32();
            var launched = reader.ReadBoolean();
            if (id <= 0 || generation <= 0 || kind is not (BuildOwnerKind.Player or BuildOwnerKind.Base) ||
                ownerId <= 0 || !seen.Add(id)) throw new InvalidDataException("Invalid build assignment");
            result.Add(new BuildTargetClaim(planetId, id, generation, kind, ownerId, launched));
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing build assignment data");
        return result;
    }
}
