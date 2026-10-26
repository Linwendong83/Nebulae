using System.IO;

namespace NebulaeModel.Networking;

internal static class PlanetRuntimeSnapshot
{
    // Matches PlanetData.ExportRuntime in the supported game. Generating terrain does not contribute
    // to this format; null mod data means an untouched planet, not a request to generate its meshes.
    public static byte[] Export(PlanetData planet)
    {
        var modifications = planet.data?.modData ?? planet.modData;
        var length = checked((planet.precision + 1) * (planet.precision + 1) * 2);
        if (modifications != null && modifications.Length != length)
            throw new InvalidDataException($"Invalid terrain modifications on planet {planet.id}");
        using var writer = new BinaryUtils.Writer();
        writer.BinaryWriter.Write(length);
        writer.BinaryWriter.Write(modifications ?? new byte[length]);
        writer.BinaryWriter.Write(0);
        writer.BinaryWriter.Write(0);
        return writer.CloseAndGetBytes();
    }
}
