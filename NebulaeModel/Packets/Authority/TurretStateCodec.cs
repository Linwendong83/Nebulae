using System.IO;
using UnityEngine;

namespace NebulaeModel.Packets.Authority;

/// <summary>Portable facts only: no host power, belt, search-pair or projectile pool indexes.</summary>
internal struct TurretState
{
    public int EntityId, ModelIndex;
    public byte Type, Group, VsSettings, Flags, MuzzleIndex, BulletInc;
    public int TargetId, TargetAstro;
    public byte TargetType;
    public short ItemId, ItemCount, ItemInc, BulletCount, ItemBulletCount, SupernovaTick;
    public int BulletDamage, RoundFire, MuzzleFire, MuzzleReset, PhasePos, AimTime, KillCount;
    public long TotalDamage, SampleTick;
    public float SupernovaStrength;
    public Vector3 Direction, TargetDirection;
}

internal static class TurretStateCodec
{
    public static bool TryEncode(in TurretState s, out byte[] bytes)
    {
        bytes = null;
        if (!Valid(s)) return false;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(s.EntityId); w.Write(s.ModelIndex);
        w.Write(s.Type); w.Write(s.Group); w.Write(s.VsSettings); w.Write(s.Flags); w.Write(s.MuzzleIndex); w.Write(s.BulletInc);
        w.Write(s.TargetId); w.Write(s.TargetAstro); w.Write(s.TargetType);
        w.Write(s.ItemId); w.Write(s.ItemCount); w.Write(s.ItemInc); w.Write(s.BulletCount); w.Write(s.ItemBulletCount); w.Write(s.SupernovaTick);
        w.Write(s.BulletDamage); w.Write(s.RoundFire); w.Write(s.MuzzleFire); w.Write(s.MuzzleReset); w.Write(s.PhasePos); w.Write(s.AimTime); w.Write(s.KillCount);
        w.Write(s.TotalDamage); w.Write(s.SampleTick); w.Write(s.SupernovaStrength);
        WriteVector(w, s.Direction); WriteVector(w, s.TargetDirection);
        bytes = stream.ToArray();
        return true;
    }

    public static bool TryDecode(byte[] bytes, out TurretState s)
    {
        s = default;
        if (bytes == null || bytes.Length > 256) return false;
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var r = new NebulaeModel.Authority.AuthorityFieldReader(stream, bytes);
            s.EntityId = r.ReadInt32(); s.ModelIndex = r.ReadInt32();
            s.Type = r.ReadByte(); s.Group = r.ReadByte(); s.VsSettings = r.ReadByte(); s.Flags = r.ReadByte(); s.MuzzleIndex = r.ReadByte(); s.BulletInc = r.ReadByte();
            s.TargetId = r.ReadInt32(); s.TargetAstro = r.ReadInt32(); s.TargetType = r.ReadByte();
            s.ItemId = r.ReadInt16(); s.ItemCount = r.ReadInt16(); s.ItemInc = r.ReadInt16(); s.BulletCount = r.ReadInt16(); s.ItemBulletCount = r.ReadInt16(); s.SupernovaTick = r.ReadInt16();
            s.BulletDamage = r.ReadInt32(); s.RoundFire = r.ReadInt32(); s.MuzzleFire = r.ReadInt32(); s.MuzzleReset = r.ReadInt32(); s.PhasePos = r.ReadInt32(); s.AimTime = r.ReadInt32(); s.KillCount = r.ReadInt32();
            s.TotalDamage = r.ReadInt64(); s.SampleTick = r.ReadInt64(); s.SupernovaStrength = r.ReadSingle();
            s.Direction = ReadVector(r); s.TargetDirection = ReadVector(r);
            return stream.Position == stream.Length && Valid(s);
        }
        catch (EndOfStreamException) { return false; }
    }

    private static bool Valid(in TurretState s) => s.EntityId > 0 && s.ModelIndex > 0 && s.ModelIndex < 2048 &&
        s.Type >= (byte)ETurretType.Gauss && s.Type <= (byte)ETurretType.Disturb && (s.Flags & ~7) == 0 && s.ItemId >= 0 && s.ItemCount >= 0 &&
        s.ItemInc >= 0 && s.BulletCount >= 0 && s.TargetId >= 0 && s.TargetAstro >= 0 &&
        s.SampleTick >= 0 && Finite(s.SupernovaStrength) && VectorFinite(s.Direction) && VectorFinite(s.TargetDirection);
    private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    private static bool VectorFinite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
    private static void WriteVector(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
    private static Vector3 ReadVector(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}
