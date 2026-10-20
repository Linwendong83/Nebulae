using System;
using System.IO;
using NebulaeModel.Networking.Serialization;

namespace NebulaeModel.Networking;

internal static class MechaAppearanceCodec
{
    private const int Header = 1;

    public static MechaAppearance Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        BinaryUtils.RequireHeader(reader, Header);
        return Read(reader, () =>
        {
            if (stream.Position != stream.Length) throw new ParseException("Trailing mecha appearance data");
        });
    }

    public static MechaAppearance ReadCompressed(byte[] bytes)
    {
        using var reader = new BinaryUtils.Reader(bytes, Header);
        return Read(reader.BinaryReader, reader.RequireEnd);
    }

    private static MechaAppearance Read(BinaryReader reader, Action requireEnd)
    {
        var appearance = new MechaAppearance();
        appearance.Init();
        try
        {
            appearance.Import(reader);
            requireEnd();
            return appearance;
        }
        catch
        {
            appearance.Free();
            throw;
        }
    }
}
