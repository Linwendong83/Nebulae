#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Authority;

#endregion

namespace NebulaeModel.Packets.Authority;

/// <summary>
/// One craft's canonical state, as the host publishes it.
/// </summary>
/// <remarks>
/// <para>
/// Identity (<see cref="ObjectKey"/> with <see cref="PoolKind.GroundCraft"/> on a planet scope or
/// <see cref="PoolKind.SpaceCraft"/> in sector scope 0) travels in the lifecycle/state framing; this
/// struct is the opaque state blob inside it. Pose and hitpoints are absolute — never deltas — and
/// fleet/unit/drone/vehicle logic components deliberately do not travel. Clients replay native fleet
/// steering from captured inputs or predict velocity between corrections, and run the shared combat
/// loops themselves for the craft they own — <c>CombatGroundSystem</c>/<c>CombatSpaceSystem</c>
/// <c>GameTick</c> and <c>PostGameTick</c> execute locally, with fleet and unit membership filtered
/// through <c>NativeCraftSimulation.FleetId</c>/<c>UnitId</c> so only owned craft is advanced and the
/// owner mecha is resolved from the host's module foreign keys. Fleet orders and dispatch, unit
/// assembly (<c>AssembleUnits_Ground</c>/<c>AssembleUnits_Space</c>), activation
/// (<c>DeterminActiveEnemyUnits</c>), craft removal, hitpoint writes, ammunition and destruction
/// remain server-owned.
/// </para>
/// <para>
/// <c>HasCombatStat == false</c> is an explicit statement (the host says this craft has no damage
/// record), not an absence. <c>Owner</c> preserves the native owner reference, including -1 for
/// player fleets. Two players' craft never share a slot and never overwrite each
/// other because every record is keyed by <see cref="ObjectKey"/>, not by owner. A craft's fleet
/// membership travels inside the native fleet blob, whose own <c>id</c> is the host's fleet slot, so
/// no separate field is carried for it: nothing on the receiving side reads one.
/// </para>
/// <para>
/// Position is double-precision so one codec serves both ground (float in vanilla, lifted
/// losslessly) and space (double) craft. Dynamic records append a clock and optional steering input;
/// static records do not pay for unused motion fields.
/// </para>
/// </remarks>
internal readonly struct CraftState
{
    public CraftState(bool hasCombatStat, bool isDynamic, bool isSpace,
        short protoId, short modelIndex, short port, byte prototype, byte stateFlags,
        int astroId, int owner,
        double posX, double posY, double posZ,
        float rotX, float rotY, float rotZ, float rotW,
        float velX, float velY, float velZ,
        int hp, int hpMax, int hpRecover, int hpIncoming, long sampleTick = 0,
        ushort playerOwner = 0, byte[] nativeFleet = null, byte[] nativeUnit = null)
    {
        HasCombatStat = hasCombatStat;
        IsDynamic = isDynamic;
        IsSpace = isSpace;
        ProtoId = protoId;
        ModelIndex = modelIndex;
        Port = port;
        Prototype = prototype;
        StateFlags = stateFlags;
        AstroId = astroId;
        Owner = owner;
        PosX = posX;
        PosY = posY;
        PosZ = posZ;
        RotX = rotX;
        RotY = rotY;
        RotZ = rotZ;
        RotW = rotW;
        VelX = velX;
        VelY = velY;
        VelZ = velZ;
        Hp = hp;
        HpMax = hpMax;
        HpRecover = hpRecover;
        HpIncoming = hpIncoming;
        SampleTick = sampleTick;
        PlayerOwner = playerOwner; NativeFleet = nativeFleet; NativeUnit = nativeUnit;
    }

    public bool HasCombatStat { get; }
    public bool IsDynamic { get; }
    public bool IsSpace { get; }
    public short ProtoId { get; }
    public short ModelIndex { get; }
    public short Port { get; }
    public byte Prototype { get; }
    public byte StateFlags { get; }
    public int AstroId { get; }
    public int Owner { get; }
    public double PosX { get; }
    public double PosY { get; }
    public double PosZ { get; }
    public float RotX { get; }
    public float RotY { get; }
    public float RotZ { get; }
    public float RotW { get; }
    public float VelX { get; }
    public float VelY { get; }
    public float VelZ { get; }
    public int Hp { get; }
    public int HpMax { get; }
    public int HpRecover { get; }
    public int HpIncoming { get; }
    public long SampleTick { get; }
    public ushort PlayerOwner { get; }
    public byte[] NativeFleet { get; }
    public byte[] NativeUnit { get; }
}

/// <summary>Encoding of <see cref="CraftState"/> into one bounded state blob.</summary>
internal static class CraftStateCodec
{
    private const byte HasCombatFlag = 1;
    private const byte IsDynamicFlag = 2;
    private const byte IsSpaceFlag = 4;

    /// <summary>Fixed wire size: flags + stateFlags + discriminants + linkage + pose + HP.</summary>
    public const int FixedSize = 1 + 1 + 7 + 8 + 52 + 16;

    public static bool TryEncode(in CraftState state, out byte[] data)
    {
        data = null;
        if (state.SampleTick < 0 || state.NativeFleet?.Length > 0 && !CraftNativeStateCodec.TryFleet(state.NativeFleet, out _) ||
            state.NativeUnit?.Length > 0 && !CraftNativeStateCodec.TryUnit(state.NativeUnit, out _) ||
            state.NativeFleet?.Length > 0 && state.NativeUnit?.Length > 0) return false;
        var flags = (byte)((state.HasCombatStat ? HasCombatFlag : 0) |
            (state.IsDynamic ? IsDynamicFlag : 0) |
            (state.IsSpace ? IsSpaceFlag : 0));
        var writer = new AuthorityPayloadWriter();
        writer.WriteByte(flags);
        writer.WriteByte(state.StateFlags);
        writer.WriteUShort(unchecked((ushort)state.ProtoId));
        writer.WriteUShort(unchecked((ushort)state.ModelIndex));
        writer.WriteUShort(unchecked((ushort)state.Port));
        writer.WriteByte(state.Prototype);
        writer.WriteInt(state.AstroId);
        writer.WriteInt(state.Owner);
        writer.WriteLong(BitConverter.DoubleToInt64Bits(state.PosX));
        writer.WriteLong(BitConverter.DoubleToInt64Bits(state.PosY));
        writer.WriteLong(BitConverter.DoubleToInt64Bits(state.PosZ));
        writer.WriteInt(WireBits.FromSingle(state.RotX));
        writer.WriteInt(WireBits.FromSingle(state.RotY));
        writer.WriteInt(WireBits.FromSingle(state.RotZ));
        writer.WriteInt(WireBits.FromSingle(state.RotW));
        writer.WriteInt(WireBits.FromSingle(state.VelX));
        writer.WriteInt(WireBits.FromSingle(state.VelY));
        writer.WriteInt(WireBits.FromSingle(state.VelZ));
        writer.WriteInt(state.Hp);
        writer.WriteInt(state.HpMax);
        writer.WriteInt(state.HpRecover);
        writer.WriteInt(state.HpIncoming);
        writer.WriteLong(state.SampleTick); writer.WriteUShort(state.PlayerOwner);
        if (!writer.TryWriteBytes(state.NativeFleet ?? Array.Empty<byte>(), CraftNativeStateCodec.MaxBytes) ||
            !writer.TryWriteBytes(state.NativeUnit ?? Array.Empty<byte>(), CraftNativeStateCodec.MaxBytes)) return false;
        data = writer.ToArray();
        return data.Length == SizeFor(state) && data.Length <= AuthorityLimits.StateRecordMaxBytes;
    }

    public static int SizeFor(in CraftState state) => FixedSize + 18 + (state.NativeFleet?.Length ?? 0) + (state.NativeUnit?.Length ?? 0);

    public static bool TryDecode(byte[] source, int offset, int length, out CraftState state,
        out AuthorityReject reject)
    {
        state = default;
        reject = AuthorityReject.Accepted;
        if (!AuthorityPayloadReader.TryCreate(source, offset, length, out var reader, out reject)) return false;
        if (!reader.TryReadByte(out var flags))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing craft flags");
            return false;
        }
        if ((flags & ~(HasCombatFlag | IsDynamicFlag | IsSpaceFlag)) != 0)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "craft flags=" + flags);
            return false;
        }
        if (!reader.TryReadByte(out var stateFlags))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing state flags");
            return false;
        }
        if (!reader.TryReadUShort(out var protoRaw) || !reader.TryReadUShort(out var modelRaw) ||
            !reader.TryReadUShort(out var portRaw))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated discriminants");
            return false;
        }
        if (!reader.TryReadByte(out var prototype))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated prototype");
            return false;
        }
        if (!reader.TryReadInt(out var astroId) || !reader.TryReadInt(out var owner))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated linkage");
            return false;
        }
        if (!reader.TryReadLong(out var posXBits) || !reader.TryReadLong(out var posYBits) ||
            !reader.TryReadLong(out var posZBits))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated position");
            return false;
        }
        if (!reader.TryReadInt(out var rotXBits) || !reader.TryReadInt(out var rotYBits) ||
            !reader.TryReadInt(out var rotZBits) || !reader.TryReadInt(out var rotWBits) ||
            !reader.TryReadInt(out var velXBits) || !reader.TryReadInt(out var velYBits) ||
            !reader.TryReadInt(out var velZBits))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated pose");
            return false;
        }
        if (!reader.TryReadInt(out var hp) || !reader.TryReadInt(out var hpMax) ||
            !reader.TryReadInt(out var hpRecover) || !reader.TryReadInt(out var hpIncoming))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated hp");
            return false;
        }
        long sampleTick = 0;
        if (!reader.TryReadLong(out sampleTick) || sampleTick < 0 || !reader.TryReadUShort(out var playerOwner) ||
            !reader.TryReadBytes(CraftNativeStateCodec.MaxBytes, out var fleet) ||
            !reader.TryReadBytes(CraftNativeStateCodec.MaxBytes, out var unit) ||
            fleet.Length > 0 && !CraftNativeStateCodec.TryFleet(fleet, out _) ||
            unit.Length > 0 && !CraftNativeStateCodec.TryUnit(unit, out _) || fleet.Length > 0 && unit.Length > 0)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid craft motion anchor");
            return false;
        }
        if (!reader.EndOfPayload)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "trailing bytes");
            return false;
        }
        state = new CraftState(
            (flags & HasCombatFlag) != 0, (flags & IsDynamicFlag) != 0, (flags & IsSpaceFlag) != 0,
            unchecked((short)protoRaw), unchecked((short)modelRaw), unchecked((short)portRaw),
            prototype, stateFlags, astroId, owner,
            BitConverter.Int64BitsToDouble(posXBits),
            BitConverter.Int64BitsToDouble(posYBits),
            BitConverter.Int64BitsToDouble(posZBits),
            WireBits.ToSingle(rotXBits),
            WireBits.ToSingle(rotYBits),
            WireBits.ToSingle(rotZBits),
            WireBits.ToSingle(rotWBits),
            WireBits.ToSingle(velXBits),
            WireBits.ToSingle(velYBits),
            WireBits.ToSingle(velZBits),
            hp, hpMax, hpRecover, hpIncoming, sampleTick, playerOwner, fleet, unit);
        return true;
    }
}
