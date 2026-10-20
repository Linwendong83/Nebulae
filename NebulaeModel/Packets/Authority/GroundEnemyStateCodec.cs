#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Authority;

#endregion

namespace NebulaeModel.Packets.Authority;

/// <summary>
/// Which ground component an enemy carries.
/// </summary>
/// <remarks>
/// <para>
/// Each value is an explicit wire discriminant. Component types travel as this enum, independently
/// of native field names.
/// </para>
/// <para>
/// Priority when several component ids are non-zero is fixed in
/// <c>GroundEnemyKinds.FromComponentIds</c>: base core first, then unit, builder, turret, shield,
/// connector, replicator. The host and the tests share that deterministic derivation.
/// </para>
/// </remarks>
internal enum GroundEnemyKind : byte
{
    Unknown = 0,
    GroundUnit = 1,
    GroundBuilder = 2,
    GroundBase = 3,
    GroundTurret = 4,
    GroundShield = 5,
    GroundConnector = 6,
    GroundReplicator = 7
}

/// <summary>Derives a wire kind from vanilla ground component ids without reflection.</summary>
internal static class GroundEnemyKinds
{
    public static GroundEnemyKind FromComponentIds(int dfGBaseId, int unitId, int builderId,
        int dfGTurretId, int dfGShieldId, int dfGConnectorId, int dfGReplicatorId)
    {
        if (dfGBaseId > 0) return GroundEnemyKind.GroundBase;
        if (unitId > 0) return GroundEnemyKind.GroundUnit;
        if (builderId > 0) return GroundEnemyKind.GroundBuilder;
        if (dfGTurretId > 0) return GroundEnemyKind.GroundTurret;
        if (dfGShieldId > 0) return GroundEnemyKind.GroundShield;
        if (dfGConnectorId > 0) return GroundEnemyKind.GroundConnector;
        if (dfGReplicatorId > 0) return GroundEnemyKind.GroundReplicator;
        return GroundEnemyKind.Unknown;
    }

    public static bool IsDefined(GroundEnemyKind kind) => kind >= GroundEnemyKind.GroundUnit &&
        kind <= GroundEnemyKind.GroundReplicator;
}

/// <summary>
/// One ground enemy's canonical state, as the host publishes it.
/// </summary>
/// <remarks>
/// <para>
/// Identity (<see cref="ObjectKey"/> with <see cref="PoolKind.GroundEnemy"/>) travels in the
/// lifecycle/state framing; this struct is the opaque state blob inside it. Pose and hitpoints are
/// absolute — never deltas. Most component internals (hatred, cooldowns, shield energy) deliberately
/// do not travel: between anchors the client advances the unit's own behavior natively —
/// <c>EnemyDFGroundSystem.GameTickLogic_Unit</c> runs whenever the planet's ground-enemy, base and
/// ground-craft scopes are live — while activation, base manufacture
/// (<c>GameTickLogic_Base</c>), hatred accumulation, damage and lifecycle stay host-owned.
/// Two exceptions exist for the vanilla brief panel: the building
/// presentation of a builder or replicator (<see cref="BuilderSp"/>, <see cref="ProductSp"/>, matter
/// and energy), which the panel divides without a zero guard, and a unit's
/// <see cref="UnitBehavior"/>, which the panel prints as its state line. A unit shell does allocate
/// one <c>EnemyUnitComponent</c> for motion, animation and native picking; the system-level AI entry
/// points (<c>KeyTickLogic</c> and the base/anim loops) stay host-owned. A third
/// exception is a turret's <see cref="TurretState"/> and <see cref="TurretTargetInShootRange"/>, which
/// the panel prints as "警戒中"/"交火中"; a shell has no turret component to read them from.
/// </para>
/// <para>
/// <c>HasCombatStat == false</c> is an explicit statement (the host says this enemy has no damage
/// record), not an absence. Base
/// linkage (<c>BaseId</c>) names the host's base-component slot; the client resolves ordering against
/// the base-core enemies it has already applied and defers the child rather than guessing (the
/// parent-child inversion rule).
/// </para>
/// <para>
/// The layout is fixed and field-by-field: kind, flags, vanilla discriminants, linkage,
/// pose, hitpoints. No reflection over names, no per-record framing beyond the flags byte.
/// </para>
/// </remarks>
internal readonly struct GroundEnemyState
{
    public GroundEnemyState(GroundEnemyKind kind, bool hasCombatStat, bool isDynamic,
        short protoId, short modelIndex, short owner, short port, byte stateFlags,
        int astroId, int originAstroId, int baseId, int builderIndex, int level,
        float posX, float posY, float posZ,
        float rotX, float rotY, float rotZ, float rotW,
        float velX, float velY, float velZ,
        int hp, int hpMax, int hpRecover, int hpIncoming,
        float animationTime = 0, float prepareLength = 0, float workingLength = 0, uint animationState = 0, float animationPower = 0,
        float unitAnimation = 0, float unitDisturb = 0, float unitSteering = 0, float unitSpeed = 0,
        int builderSp = 0, int builderSpMax = 0, int matter = 0, int matterMax = 0,
        int energy = 0, int energyMax = 0, int matterChange = 0, int energyChange = 0,
        int productSp = 0, int productSpMax = 0, byte unitBehavior = 0,
        byte turretState = 0, bool turretTargetInShootRange = false, long sampleTick = 0, int unitStateTick = 0,
        byte[] nativeUnit = null, int builderState = 0, byte[] nativeTurret = null, bool turretCounterAttack = false,
        EnemyUnitComponent? decodedNativeUnit = null)
    {
        Kind = kind;
        HasCombatStat = hasCombatStat;
        IsDynamic = isDynamic;
        ProtoId = protoId;
        ModelIndex = modelIndex;
        Owner = owner;
        Port = port;
        StateFlags = stateFlags;
        AstroId = astroId;
        OriginAstroId = originAstroId;
        BaseId = baseId;
        BuilderIndex = builderIndex;
        Level = level;
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
        AnimationTime = animationTime; PrepareLength = prepareLength; WorkingLength = workingLength;
        AnimationState = animationState; AnimationPower = animationPower;
        UnitAnimation = unitAnimation; UnitDisturb = unitDisturb; UnitSteering = unitSteering; UnitSpeed = unitSpeed;
        BuilderSp = builderSp; BuilderSpMax = builderSpMax; Matter = matter; MatterMax = matterMax;
        Energy = energy; EnergyMax = energyMax; MatterChange = matterChange; EnergyChange = energyChange;
        ProductSp = productSp; ProductSpMax = productSpMax;
        UnitBehavior = unitBehavior;
        TurretState = turretState;
        TurretTargetInShootRange = turretTargetInShootRange;
        TurretCounterAttack = turretCounterAttack;
        SampleTick = sampleTick;
        UnitStateTick = unitStateTick;
        NativeUnit = nativeUnit;
        DecodedNativeUnit = decodedNativeUnit;
        BuilderState = builderState; NativeTurret = nativeTurret;
    }

    internal GroundEnemyState(in GroundEnemyState source, byte[] nativeUnit, in EnemyUnitComponent unit)
    {
        this = source; NativeUnit = nativeUnit; DecodedNativeUnit = unit;
    }

    public GroundEnemyKind Kind { get; }
    public bool HasCombatStat { get; }
    public bool IsDynamic { get; }
    public short ProtoId { get; }
    public short ModelIndex { get; }
    public short Owner { get; }
    public short Port { get; }
    public byte StateFlags { get; }
    public int AstroId { get; }
    public int OriginAstroId { get; }
    public int BaseId { get; }
    public int BuilderIndex { get; }
    public int Level { get; }
    public float PosX { get; }
    public float PosY { get; }
    public float PosZ { get; }
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
    public float AnimationTime { get; }
    public float PrepareLength { get; }
    public float WorkingLength { get; }
    public uint AnimationState { get; }
    public float AnimationPower { get; }
    public float UnitAnimation { get; }
    public float UnitDisturb { get; }
    public float UnitSteering { get; }
    public float UnitSpeed { get; }
    public int BuilderSp { get; }
    public int BuilderSpMax { get; }
    public int Matter { get; }
    public int MatterMax { get; }
    public int Energy { get; }
    public int EnergyMax { get; }
    public int MatterChange { get; }
    public int EnergyChange { get; }
    public int ProductSp { get; }
    public int ProductSpMax { get; }

    /// <summary>
    /// The unit's vanilla <c>EnemyUnitComponent.behavior</c> (the <c>EEnemyBehavior</c> value), so the
    /// client's brief panel can print the real state line ("攻击中"/"巡逻中"/"回归中") instead of the
    /// default. Zero for every non-unit enemy. The byte is part of the current wire format, so a record
    /// without it is refused rather than silently defaulted. Native motion consumes the same behavior.
    /// </summary>
    public byte UnitBehavior { get; }

    /// <summary>
    /// The turret's vanilla <c>DFGTurretComponent.state</c> (the <c>EDFTurretState</c> value) and
    /// <c>targetInShootRange</c>, so the client's brief panel prints "交火中" for an engaged turret
    /// instead of the default "警戒中" a component-less shell would show. Zero/false for every
    /// non-turret enemy; the in-range flag rides in the flags byte.
    /// </summary>
    public byte TurretState { get; }
    public bool TurretTargetInShootRange { get; }
    public bool TurretCounterAttack { get; }
    public long SampleTick { get; }
    public int UnitStateTick { get; }
    public byte[] NativeUnit { get; }
    public EnemyUnitComponent? DecodedNativeUnit { get; }
    public int BuilderState { get; }
    public byte[] NativeTurret { get; }
}

/// <summary>Encoding of <see cref="GroundEnemyState"/> into one bounded state blob.</summary>
internal static class GroundEnemyStateCodec
{
    private const byte HasCombatFlag = 1;
    private const byte IsDynamicFlag = 2;
    private const byte TurretInRangeFlag = 4;
    private const byte TurretCounterAttackFlag = 8;

    /// <summary>
    /// The largest vanilla <c>EEnemyBehavior</c> value; the wire carries it as one byte and rejects
    /// anything above, so a corrupt record cannot reach the panel. NebulaeModel deliberately does not
    /// reference the game assembly, so the value is pinned here with its vanilla name.
    /// </summary>
    public const byte MaxUnitBehavior = 9; // EEnemyBehavior.Engage

    /// <summary>The largest vanilla <c>EDFTurretState</c> value, pinned the same way.</summary>
    public const byte MaxTurretState = 2; // EDFTurretState.Aiming

    /// <summary>Fixed wire size: kind + flags + stateFlags + discriminants + linkage + pose + HP.</summary>
    public const int FixedSize = 4 + 1 + 1 + 1 + 8 + 20 + 40 + 16 + 20 + 16 + 32 + 8 + 1 + 1 + 8 + 4 + 4 + 4;

    public static bool TryEncode(in GroundEnemyState state, out byte[] data) => TryEncodeCore(state, false, out data);

    // Only this path can skip a byte round-trip: it validates the captured struct and
    // creates its own fresh native buffer. Raw caller-supplied byte arrays remain fully checked.
    internal static bool TryEncodeCaptured(in GroundEnemyState state, in EnemyUnitComponent unit,
        out byte[] data, out GroundEnemyState canonical)
    {
        data = null; canonical = state;
        if (state.Kind != GroundEnemyKind.GroundUnit) return TryEncode(state, out data);
        if (!EnemyUnitStateCodec.IsValid(unit)) return false;
        canonical = new GroundEnemyState(state, EnemyUnitStateCodec.Encode(unit), unit);
        return TryEncodeCore(canonical, true, out data);
    }

    private static bool TryEncodeCore(in GroundEnemyState state, bool capturedNative, out byte[] data)
    {
        data = null;
        if (!GroundEnemyKinds.IsDefined(state.Kind) || state.SampleTick < 0) return false;
        if (state.BuilderSp < 0 || state.BuilderSpMax < 0 || state.BuilderSp > state.BuilderSpMax ||
            state.ProductSp < 0 || state.ProductSpMax < 0 || state.ProductSp > state.ProductSpMax ||
            state.MatterMax < 0 || state.EnergyMax < 0 || state.UnitBehavior > MaxUnitBehavior ||
            state.TurretState > MaxTurretState) return false;
        var flags = (byte)((state.HasCombatStat ? HasCombatFlag : 0) | (state.IsDynamic ? IsDynamicFlag : 0) |
            (state.TurretTargetInShootRange ? TurretInRangeFlag : 0) | (state.TurretCounterAttack ? TurretCounterAttackFlag : 0));
        var native = state.NativeUnit ?? Array.Empty<byte>();
        var turret = state.NativeTurret ?? Array.Empty<byte>();
        if (native.Length > EnemyUnitStateCodec.MaxBytes || turret.Length > 512) return false;
        var writer = new AuthorityPayloadWriter(FixedSize + native.Length + turret.Length);
        writer.WriteByte((byte)state.Kind);
        writer.WriteByte(flags);
        writer.WriteByte(state.StateFlags);
        writer.WriteUShort(unchecked((ushort)state.ProtoId));
        writer.WriteUShort(unchecked((ushort)state.ModelIndex));
        writer.WriteUShort(unchecked((ushort)state.Owner));
        writer.WriteUShort(unchecked((ushort)state.Port));
        writer.WriteInt(state.AstroId);
        writer.WriteInt(state.OriginAstroId);
        writer.WriteInt(state.BaseId);
        writer.WriteInt(state.BuilderIndex);
        writer.WriteInt(state.Level);
        writer.WriteInt(WireBits.FromSingle(state.PosX));
        writer.WriteInt(WireBits.FromSingle(state.PosY));
        writer.WriteInt(WireBits.FromSingle(state.PosZ));
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
        writer.WriteInt(WireBits.FromSingle(state.AnimationTime));
        writer.WriteInt(WireBits.FromSingle(state.PrepareLength));
        writer.WriteInt(WireBits.FromSingle(state.WorkingLength));
        writer.WriteInt(unchecked((int)state.AnimationState));
        writer.WriteInt(state.BuilderState);
        writer.WriteInt(WireBits.FromSingle(state.AnimationPower));
        writer.WriteInt(WireBits.FromSingle(state.UnitAnimation));
        writer.WriteInt(WireBits.FromSingle(state.UnitDisturb));
        writer.WriteInt(WireBits.FromSingle(state.UnitSteering));
        writer.WriteInt(WireBits.FromSingle(state.UnitSpeed));
        writer.WriteInt(state.BuilderSp); writer.WriteInt(state.BuilderSpMax);
        writer.WriteInt(state.Matter); writer.WriteInt(state.MatterMax);
        writer.WriteInt(state.Energy); writer.WriteInt(state.EnergyMax);
        writer.WriteInt(state.MatterChange); writer.WriteInt(state.EnergyChange);
        writer.WriteInt(state.ProductSp); writer.WriteInt(state.ProductSpMax);
        writer.WriteByte(state.UnitBehavior);
        writer.WriteByte(state.TurretState);
        writer.WriteLong(state.SampleTick);
        writer.WriteInt(state.UnitStateTick);
        if (native.Length > 0 && (state.Kind != GroundEnemyKind.GroundUnit || !capturedNative && !EnemyUnitStateCodec.TryDecode(native, out _))) return false;
        if (!writer.TryWriteBytes(native, EnemyUnitStateCodec.MaxBytes)) return false;
        if (!EnemyTurretStateCodec.Valid(turret, false, out var counterAttack) ||
            state.TurretCounterAttack && !counterAttack || !writer.TryWriteBytes(turret, 512)) return false;
        data = writer.Finish();
        if (data.Length != FixedSize + native.Length + turret.Length || data.Length > AuthorityLimits.StateRecordMaxBytes) return false;
        AuthorityStateFields.RememberEncoded(PoolKind.GroundEnemy, data);
        return true;
    }

    public static bool TryDecode(byte[] source, int offset, int length, out GroundEnemyState state,
        out AuthorityReject reject)
    {
        state = default;
        reject = AuthorityReject.Accepted;
        if (!AuthorityPayloadReader.TryCreate(source, offset, length, out var reader, out reject)) return false;
        if (!reader.TryReadByte(out var kindRaw) || !GroundEnemyKinds.IsDefined((GroundEnemyKind)kindRaw))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "ground enemy kind=" + kindRaw);
            return false;
        }
        if (!reader.TryReadByte(out var flags))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing ground enemy flags");
            return false;
        }
        if ((flags & ~(HasCombatFlag | IsDynamicFlag | TurretInRangeFlag | TurretCounterAttackFlag)) != 0)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "ground enemy flags=" + flags);
            return false;
        }
        if (!reader.TryReadByte(out var stateFlags))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing state flags");
            return false;
        }
        if (!reader.TryReadUShort(out var protoRaw) || !reader.TryReadUShort(out var modelRaw) ||
            !reader.TryReadUShort(out var ownerRaw) || !reader.TryReadUShort(out var portRaw))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated discriminants");
            return false;
        }
        if (!reader.TryReadInt(out var astroId) || !reader.TryReadInt(out var originAstroId) ||
            !reader.TryReadInt(out var baseId) || !reader.TryReadInt(out var builderIndex) ||
            !reader.TryReadInt(out var level))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated linkage");
            return false;
        }
        if (!reader.TryReadInt(out var posXBits) || !reader.TryReadInt(out var posYBits) ||
            !reader.TryReadInt(out var posZBits) || !reader.TryReadInt(out var rotXBits) ||
            !reader.TryReadInt(out var rotYBits) || !reader.TryReadInt(out var rotZBits) ||
            !reader.TryReadInt(out var rotWBits) || !reader.TryReadInt(out var velXBits) ||
            !reader.TryReadInt(out var velYBits) || !reader.TryReadInt(out var velZBits))
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
        if (!reader.TryReadInt(out var animationTime) || !reader.TryReadInt(out var prepareLength) ||
            !reader.TryReadInt(out var workingLength) || !reader.TryReadInt(out var animationState) || !reader.TryReadInt(out var builderState) || !reader.TryReadInt(out var animationPower))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated animation"); return false;
        }
        if (!reader.TryReadInt(out var unitAnimation) || !reader.TryReadInt(out var unitDisturb) ||
            !reader.TryReadInt(out var unitSteering) || !reader.TryReadInt(out var unitSpeed))
        { reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated unit visual"); return false; }
        if (!reader.TryReadInt(out var builderSp) || !reader.TryReadInt(out var builderSpMax) ||
            !reader.TryReadInt(out var matter) || !reader.TryReadInt(out var matterMax) ||
            !reader.TryReadInt(out var energy) || !reader.TryReadInt(out var energyMax) ||
            !reader.TryReadInt(out var matterChange) || !reader.TryReadInt(out var energyChange) ||
            !reader.TryReadInt(out var productSp) || !reader.TryReadInt(out var productSpMax) ||
            builderSp < 0 || builderSpMax < 0 || builderSp > builderSpMax ||
            productSp < 0 || productSpMax < 0 || productSp > productSpMax ||
            matterMax < 0 || energyMax < 0)
        { reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid building presentation"); return false; }
        if (!reader.TryReadByte(out var unitBehavior) || unitBehavior > MaxUnitBehavior)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid unit behavior");
            return false;
        }
        if (!reader.TryReadByte(out var turretState) || turretState > MaxTurretState)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid turret state");
            return false;
        }
        if (!reader.TryReadLong(out var sampleTick) || sampleTick < 0 || !reader.TryReadInt(out var unitStateTick))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid ground motion anchor");
            return false;
        }
        EnemyUnitComponent nativeComponent = default;
        if (!reader.TryReadBytes(EnemyUnitStateCodec.MaxBytes, out var nativeUnit) ||
            nativeUnit.Length > 0 && ((GroundEnemyKind)kindRaw != GroundEnemyKind.GroundUnit ||
                !EnemyUnitStateCodec.TryDecode(nativeUnit, out nativeComponent)))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid native ground unit");
            return false;
        }
        if (!reader.TryReadBytes(512, out var nativeTurret) || !EnemyTurretStateCodec.Valid(nativeTurret, false, out var counterAttack))
        { reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid native turret"); return false; }
        if ((flags & TurretCounterAttackFlag) != 0 && !counterAttack)
        { reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid turret retaliation target"); return false; }
        if (!reader.EndOfPayload)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "trailing bytes");
            return false;
        }
        state = new GroundEnemyState(
            (GroundEnemyKind)kindRaw, (flags & HasCombatFlag) != 0, (flags & IsDynamicFlag) != 0,
            unchecked((short)protoRaw), unchecked((short)modelRaw),
            unchecked((short)ownerRaw), unchecked((short)portRaw), stateFlags,
            astroId, originAstroId, baseId, builderIndex, level,
            WireBits.ToSingle(posXBits),
            WireBits.ToSingle(posYBits),
            WireBits.ToSingle(posZBits),
            WireBits.ToSingle(rotXBits),
            WireBits.ToSingle(rotYBits),
            WireBits.ToSingle(rotZBits),
            WireBits.ToSingle(rotWBits),
            WireBits.ToSingle(velXBits),
            WireBits.ToSingle(velYBits),
            WireBits.ToSingle(velZBits),
            hp, hpMax, hpRecover, hpIncoming,
            WireBits.ToSingle(animationTime),
            WireBits.ToSingle(prepareLength),
            WireBits.ToSingle(workingLength),
            unchecked((uint)animationState), WireBits.ToSingle(animationPower),
            WireBits.ToSingle(unitAnimation), WireBits.ToSingle(unitDisturb),
            WireBits.ToSingle(unitSteering), WireBits.ToSingle(unitSpeed),
            builderSp, builderSpMax, matter, matterMax, energy, energyMax, matterChange, energyChange,
            productSp, productSpMax, unitBehavior, turretState, (flags & TurretInRangeFlag) != 0, sampleTick, unitStateTick, nativeUnit, builderState, nativeTurret,
            (flags & TurretCounterAttackFlag) != 0, nativeUnit.Length > 0 ? nativeComponent : (EnemyUnitComponent?)null);
        return true;
    }

}
