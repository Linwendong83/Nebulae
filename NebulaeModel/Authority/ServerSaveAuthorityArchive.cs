using System;
using System.IO;
using NebulaeModel.Networking.Serialization;

namespace NebulaeModel.Authority;

/// <summary>Preserves legacy authority facts inside the existing server save.</summary>
internal static class ServerSaveAuthorityArchive
{
    // NBAS already identifies and bounds the historical schema. The optional trailing block
    // leaves the server revision and player wire layout unchanged; older readers ignore it.
    public static void Append(NetDataWriter writer, byte[] archive, string worldId)
    {
        if (archive == null || archive.Length == 0) return;
        Validate(archive, worldId);
        writer.Put(archive);
    }

    public static byte[] Read(NetDataReader reader, string worldId)
    {
        if (reader.AvailableBytes == 0) return null;
        if (reader.AvailableBytes > AuthoritySidecarLimits.FileMaxBytes)
            throw new InvalidDataException("Authority archive exceeds the save size ceiling");
        var archive = reader.GetRemainingBytes();
        Validate(archive, worldId);
        return archive;
    }

    public static void Validate(byte[] archive, string worldId)
    {
        if (!AuthoritySaveCodec.TryDecode(archive, out var state, out var reason))
            throw new InvalidDataException("Invalid authority archive: " + reason);
        if (!string.Equals(state.WorldId, worldId, StringComparison.Ordinal))
            throw new InvalidDataException("Authority archive belongs to a different world");
    }

    public static string[] LegacyPaths(string serverPath) =>
        new[] { serverPath + ".server.authority", serverPath + ".authority" };
}
