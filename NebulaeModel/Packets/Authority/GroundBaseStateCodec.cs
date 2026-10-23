using System.IO;

namespace NebulaeModel.Packets.Authority;

/// <summary>Host facts needed by the native base and formation renderers, never client AI.</summary>
[NebulaeModel.Networking.PacketId(1889181343075583294UL)]
internal sealed class GroundBaseState
{
    public int EnemyId, RuinId, Incoming, Buildings, Units;
    public long RuinGeneration;
    public int HiveAstroId, RelayEnemyId;
    public byte RemovalFlags; // Native CheckBaseCanRemoved: core (1), other buildings (2), formations (4).
    public bool CoreAlive;
    public byte[] Evolution;
    public float X, Y, Z;
    public byte[][] Forms;
}

internal static class GroundBaseStateCodec
{
    public const int MaxPayload = NebulaeModel.Authority.AuthorityLimits.StateRecordMaxBytes;
    public const int MaxEvolutionBytes = 128;
    public static int PortCount(int form) => form switch { 0 => 72, 1 => 108, 2 => 48, _ => 0 };

    public static byte[] Encode(GroundBaseState state)
    {
        Validate(state);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(state.EnemyId); writer.Write(state.RuinId);
        writer.Write(state.RuinGeneration);
        writer.Write(state.CoreAlive);
        writer.Write(state.RemovalFlags); writer.Write(state.HiveAstroId); writer.Write(state.RelayEnemyId);
        writer.Write((ushort)state.Evolution.Length); writer.Write(state.Evolution);
        writer.Write(state.Incoming); writer.Write(state.X); writer.Write(state.Y); writer.Write(state.Z);
        writer.Write(state.Buildings); writer.Write(state.Units);
        for (var form = 0; form < 3; form++)
        {
            var tags = state.Forms[form];
            writer.Write((ushort)tags.Length);
            for (var port = 0; port < tags.Length; port += 4)
                writer.Write((byte)(tags[port] | tags[port + 1] << 2 | tags[port + 2] << 4 | tags[port + 3] << 6));
        }
        return stream.ToArray();
    }

    public static GroundBaseState Decode(byte[] payload)
    {
        if (payload == null || payload.Length > MaxPayload) throw new InvalidDataException("Invalid base payload size.");
        using var stream = new MemoryStream(payload, false);
        using var reader = new NebulaeModel.Authority.AuthorityFieldReader(stream, payload);
        var state = new GroundBaseState { EnemyId = reader.ReadInt32(), RuinId = reader.ReadInt32() };
        state.RuinGeneration = reader.ReadInt64();
        state.CoreAlive = reader.ReadBoolean();
        state.RemovalFlags = reader.ReadByte(); state.HiveAstroId = reader.ReadInt32(); state.RelayEnemyId = reader.ReadInt32();
        var evolutionLength = reader.ReadUInt16();
        if (evolutionLength > MaxEvolutionBytes) throw new InvalidDataException("Invalid base evolution size.");
        state.Evolution = reader.ReadBytes(evolutionLength);
        if (state.Evolution.Length != evolutionLength) throw new EndOfStreamException();
        state.Incoming = reader.ReadInt32(); state.X = reader.ReadSingle(); state.Y = reader.ReadSingle(); state.Z = reader.ReadSingle();
        state.Buildings = reader.ReadInt32(); state.Units = reader.ReadInt32(); state.Forms = new byte[3][];
        for (var form = 0; form < 3; form++)
        {
            var length = reader.ReadUInt16();
            if (length != PortCount(form)) throw new InvalidDataException("Invalid formation port count.");
            state.Forms[form] = new byte[length];
            for (var port = 0; port < length; port += 4)
            {
                var packed = reader.ReadByte();
                for (var part = 0; part < 4; part++) state.Forms[form][port + part] = (byte)((packed >> (part * 2)) & 3);
            }
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing base state data.");
        Validate(state);
        return state;
    }

    private static void Validate(GroundBaseState state)
    {
        if (state == null || state.EnemyId <= 0 || state.RuinId < 0 || state.RuinGeneration < 0 || state.Incoming < 0 || state.RemovalFlags > 7 ||
            state.RelayEnemyId < 0 || (state.HiveAstroId != 0 && !NebulaeModel.Authority.AuthorityScope.IsSectorAstro(state.HiveAstroId)) ||
            state.Buildings < 0 || state.Units < 0 || state.Evolution == null || state.Evolution.Length > MaxEvolutionBytes ||
            !Finite(state.X) || !Finite(state.Y) || !Finite(state.Z) || state.Forms?.Length != 3)
            throw new InvalidDataException("Invalid base presentation state.");
        for (var form = 0; form < 3; form++)
        {
            if (state.Forms[form]?.Length != PortCount(form)) throw new InvalidDataException("Invalid formation port count.");
            foreach (var tag in state.Forms[form]) if (tag > 3) throw new InvalidDataException("Invalid formation port tag.");
        }
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
