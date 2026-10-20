using System.IO;

namespace NebulaeModel.Packets.Authority;

/// <summary>Native hive formation port tags, packed into two bits per port.</summary>
internal static class HiveFormationStateCodec
{
    public static int PortCount(int form) => form switch { 0 => 1440, 1 => 120, 2 => 6, _ => throw new InvalidDataException("Invalid hive formation.") };

    public static void Write(BinaryWriter writer, int form, int[] units)
    {
        var count = PortCount(form);
        if (units?.Length != count + 1) throw new InvalidDataException("Invalid hive formation port count.");
        for (var port = 1; port <= count; port += 4)
        {
            byte packed = 0;
            for (var part = 0; part < 4 && port + part <= count; part++)
            {
                var tag = units[port + part];
                if ((uint)tag > 3) throw new InvalidDataException("Invalid hive formation tag.");
                packed |= (byte)(tag << (part * 2));
            }
            writer.Write(packed);
        }
    }

    public static byte[] Read(BinaryReader reader, int form)
    {
        var count = PortCount(form);
        var packed = reader.ReadBytes((count + 3) / 4);
        if (packed.Length != (count + 3) / 4) throw new EndOfStreamException();
        if (count % 4 != 0 && (packed[packed.Length - 1] >> (count % 4 * 2)) != 0)
            throw new InvalidDataException("Invalid hive formation padding.");
        var tags = new byte[count];
        for (var port = 0; port < count; port++) tags[port] = (byte)((packed[port / 4] >> (port % 4 * 2)) & 3);
        return tags;
    }
}
