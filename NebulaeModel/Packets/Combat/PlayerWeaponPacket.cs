using System;
using System.Collections.Generic;
using System.IO;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;

namespace NebulaeModel.Packets.Combat;

internal readonly struct PlayerShotId : IEquatable<PlayerShotId>
{
    public PlayerShotId(ushort player, ulong connection, long sequence)
    { Player = player; Connection = connection; Sequence = sequence; }
    public ushort Player { get; }
    public ulong Connection { get; }
    public long Sequence { get; }
    public bool Equals(PlayerShotId other) => Player == other.Player && Connection == other.Connection && Sequence == other.Sequence;
    public override bool Equals(object obj) => obj is PlayerShotId other && Equals(other);
    public override int GetHashCode() => Player ^ Connection.GetHashCode() ^ Sequence.GetHashCode();
}

internal enum PlayerWeaponEvent : byte { Launch, State, Impact, End, Cancel, Settled }

internal sealed class PlayerWeaponSample
{
    public PlayerShotId Shot;
    public int Part, Ordinal;
    public PlayerWeaponEvent Event;
    public BattleEffectKind Kind;
    public long Tick, ResourceRevision;
    public int Star, Astro, Subtype;
    public ObjectKey Target;
    public byte[] Payload = Array.Empty<byte>();

    public static byte[] Encode(IReadOnlyList<PlayerWeaponSample> samples)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(samples.Count);
        foreach (var s in samples)
        {
            w.Write(s.Shot.Player); w.Write(s.Shot.Connection); w.Write(s.Shot.Sequence);
            w.Write(s.Part); w.Write(s.Ordinal); w.Write((byte)s.Event); w.Write((byte)s.Kind); w.Write(s.Tick); w.Write(s.ResourceRevision);
            w.Write(s.Star); w.Write(s.Astro); w.Write(s.Subtype);
            var target = new byte[AuthorityLimits.ObjectKeyBytes]; ObjectKeyCodec.WriteTo(target, 0, s.Target); w.Write(target);
            w.Write(s.Payload.Length); w.Write(s.Payload);
        }
        return stream.ToArray();
    }

    public static List<PlayerWeaponSample> Decode(byte[] data)
    {
        if (data == null || data.Length < 4 || data.Length > 4 * 1024 * 1024) throw new InvalidDataException("Weapon frame length");
        using var stream = new MemoryStream(data, false); using var r = new BinaryReader(stream);
        var count = r.ReadInt32();
        if (count < 0 || count > 16384) throw new InvalidDataException("Weapon frame count");
        var result = new List<PlayerWeaponSample>(count);
        for (var i = 0; i < count; i++)
        {
            var s = new PlayerWeaponSample
            {
                Shot = new PlayerShotId(r.ReadUInt16(), r.ReadUInt64(), r.ReadInt64()), Part = r.ReadInt32(), Ordinal = r.ReadInt32(),
                Event = (PlayerWeaponEvent)r.ReadByte(), Kind = (BattleEffectKind)r.ReadByte(),
                Tick = r.ReadInt64(), ResourceRevision = r.ReadInt64(), Star = r.ReadInt32(), Astro = r.ReadInt32(), Subtype = r.ReadInt32()
            };
            var key = r.ReadBytes(AuthorityLimits.ObjectKeyBytes);
            s.Target = ObjectKeyCodec.ReadRaw(key, 0);
            var length = r.ReadInt32();
            if (s.Shot.Player == 0 || s.Shot.Sequence <= 0 || s.Part < 0 || s.Tick < 0 || s.ResourceRevision < 0 ||
                s.Event > PlayerWeaponEvent.Settled || !Enum.IsDefined(typeof(BattleEffectKind), s.Kind) ||
                length < 0 || length > 65536 || length > stream.Length - stream.Position) throw new InvalidDataException("Weapon sample");
            s.Payload = r.ReadBytes(length); result.Add(s);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing weapon data");
        return result;
    }
}

[PacketId(11837019735064218591UL)]
internal sealed class PlayerWeaponPacket
{
    [WireOrder(0)] public ulong EpochHigh { get; set; }
    [WireOrder(1)] public ulong EpochLow { get; set; }
    [WireOrder(2)] public byte[] Data { get; set; }
}
