using System.IO;

namespace NebulaeModel.Packets.Authority;

internal enum TurretIntentKind : byte { Group = 1, Priority, Phase, Supernova, Deposit, Withdraw, Clear }

internal readonly struct TurretIntent
{
    public const byte Category = 2;
    public TurretIntent(TurretIntentKind kind, int argument = 0, int count = 0, int inc = 0)
    { Kind = kind; Argument = argument; Count = count; Inc = inc; }
    public TurretIntentKind Kind { get; }
    public int Argument { get; }
    public int Count { get; }
    public int Inc { get; }
    public bool IsInventory => Kind >= TurretIntentKind.Deposit;
    public byte[] Encode()
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write((byte)Kind); w.Write(Argument); w.Write(Count); w.Write(Inc);
        return stream.ToArray();
    }
    public static bool TryDecode(byte[] bytes, out TurretIntent intent)
    {
        intent = default;
        if (bytes == null || bytes.Length != 13) return false;
        using var r = new BinaryReader(new MemoryStream(bytes, false));
        var kind = (TurretIntentKind)r.ReadByte(); var argument = r.ReadInt32(); var count = r.ReadInt32(); var inc = r.ReadInt32();
        if (kind < TurretIntentKind.Group || kind > TurretIntentKind.Clear || count < 0 || count > 100 || inc < 0 || inc > 1000) return false;
        intent = new TurretIntent(kind, argument, count, inc); return true;
    }
}
