using System.IO;

namespace NebulaeModel.Packets.Authority;

internal enum CombatIntentKind : byte { Shoot = 1, Bomb, ShieldBurst, Wake, FleetLaunch, FleetRecall, Settings, FleetConfigure, ActivateGroundFormation, ManualShoot }

internal readonly struct CombatIntent
{
    public const byte Category = 1;
    public CombatIntent(CombatIntentKind kind, int argument = 0, double x = 0, double y = 0, double z = 0)
    { Kind = kind; Argument = argument; X = x; Y = y; Z = z; }
    public CombatIntentKind Kind { get; }
    public int Argument { get; }
    public double X { get; }
    public double Y { get; }
    public double Z { get; }
    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)Kind); writer.Write(Argument);
        writer.Write(X); writer.Write(Y); writer.Write(Z);
        return stream.ToArray();
    }
    public static bool TryDecode(byte[] payload, out CombatIntent intent)
    {
        intent = default;
        if (payload == null || payload.Length != 29) return false;
        using var reader = new BinaryReader(new MemoryStream(payload, false));
        var kind = (CombatIntentKind)reader.ReadByte(); var argument = reader.ReadInt32();
        var x = reader.ReadDouble(); var y = reader.ReadDouble(); var z = reader.ReadDouble();
        if (kind < CombatIntentKind.Shoot || kind > CombatIntentKind.ManualShoot ||
            double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y) || double.IsNaN(z) || double.IsInfinity(z)) return false;
        intent = new CombatIntent(kind, argument, x, y, z);
        return true;
    }
}
