#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Authority;

#endregion

namespace NebulaeModel.Packets.Authority;

/// <summary>
/// Which space component an enemy carries.
/// </summary>
/// <remarks>
/// <para>
/// Each value is an explicit wire discriminant. Component types travel as this enum, independently
/// of native field names.
/// </para>
/// <para>
/// Priority when several component ids are non-zero is fixed in
/// <c>SpaceEnemyKinds.FromComponentIds</c>: relay and tinder (dynamic) first, then hive core,
/// unit, turret, gamma, node, connector, replicator, then builder. Native buildings carry both a
/// builder and their specialized component; the specialized kind preserves their presentation facts.
/// </para>
/// </remarks>
internal enum SpaceEnemyKind : byte
{
    Unknown = 0,
    SpaceUnit = 1,
    SpaceBuilder = 2,
    SpaceCore = 3,
    SpaceNode = 4,
    SpaceConnector = 5,
    SpaceReplicator = 6,
    SpaceGamma = 7,
    SpaceTurret = 8,
    SpaceRelay = 9,
    SpaceTinder = 10
}

/// <summary>Derives a wire kind from vanilla space component ids without reflection.</summary>
internal static class SpaceEnemyKinds
{
    public static SpaceEnemyKind FromComponentIds(int dfSCoreId, int dfSNodeId, int dfSConnectorId,
        int dfSReplicatorId, int dfSGammaId, int dfSTurretId, int dfTinderId, int dfRelayId,
        int unitId, int builderId)
    {
        if (dfRelayId > 0) return SpaceEnemyKind.SpaceRelay;
        if (dfTinderId > 0) return SpaceEnemyKind.SpaceTinder;
        if (dfSCoreId > 0) return SpaceEnemyKind.SpaceCore;
        if (unitId > 0) return SpaceEnemyKind.SpaceUnit;
        if (dfSTurretId > 0) return SpaceEnemyKind.SpaceTurret;
        if (dfSGammaId > 0) return SpaceEnemyKind.SpaceGamma;
        if (dfSNodeId > 0) return SpaceEnemyKind.SpaceNode;
        if (dfSConnectorId > 0) return SpaceEnemyKind.SpaceConnector;
        if (dfSReplicatorId > 0) return SpaceEnemyKind.SpaceReplicator;
        if (builderId > 0) return SpaceEnemyKind.SpaceBuilder;
        return SpaceEnemyKind.Unknown;
    }

    public static bool IsDefined(SpaceEnemyKind kind) => kind >= SpaceEnemyKind.SpaceUnit &&
        kind <= SpaceEnemyKind.SpaceTinder;
}

/// <summary>
/// One space enemy's canonical state, as the host publishes it.
/// </summary>
/// <remarks>
/// <para>
/// Identity (<see cref="ObjectKey"/> with <see cref="PoolKind.SpaceEnemy"/> in sector scope 0)
/// travels in the lifecycle/state framing; this struct is the opaque state blob inside it. Pose and
/// hitpoints are absolute — never deltas. Most component internals (hatred, cooldowns, hive evolve,
/// resource and logistics simulation) deliberately do not travel. Passive travel stages, targets
/// and core manufacture progress do travel in a kind-specific tail. Between anchors the client runs the hive's
/// unit
/// behavior natively — <c>EnemyDFHiveSystem.GameTickLogic</c> executes while the sector's
/// space-enemy, hive-summary and space-craft scopes are live — while hive manufacture, the hive's
/// decision AI (<c>DecisionAI</c>, <c>InterLearningFromLocalSystem</c>,
/// <c>InterLearningFromOtherSystem</c>), hatred accumulation and attack stay host-owned.
/// The brief panel also needs the
/// building presentation of a builder or replicator (<see cref="BuilderSp"/>, <see cref="ProductSp"/>,
/// matter and energy), which the panel divides without a zero guard, and a unit's
/// <see cref="UnitBehavior"/>, which the panel prints as its state line. A unit shell does allocate
/// one <c>EnemyUnitComponent</c> for motion, picking and presentation. The hive AI remains
/// server-owned. Another shared fact is
/// a turret's <see cref="TurretState"/> and <see cref="TurretTargetInShootRange"/>, which the panel
/// prints as "警戒中"/"交火中"; a shell has no turret component to read them from.
/// </para>
/// <para>
/// <c>HasCombatStat == false</c> is an explicit statement (the host says this enemy has no damage
/// record), not an absence. The hive
/// linkage (<c>OriginAstroId</c>) names the hive astro the enemy belongs to; the current
/// <c>AstroId</c> is where it is now. A relay that lands and becomes a ground base is a different
/// pool (GroundEnemy) under a new key — the space key despawns and the ground key spawns — so no
/// binding ever reinterprets one pool's slot as another's. Cross-astro migration inside the sector
/// keeps the same key: the sector scope is 0 and the astro ids are attributes, not identity.
/// </para>
/// <para>
/// Position is double-precision (sector coordinates reach 1e9); rotation/velocity stay float like
/// vanilla. The layout is fixed and field-by-field: kind, flags, discriminants, linkage,
/// pose, hitpoints, display animation and building presentation. No reflection over names.
/// </para>
/// </remarks>
internal readonly struct SpaceEnemyState
{
    public SpaceEnemyState(SpaceEnemyKind kind, bool hasCombatStat, bool isDynamic,
        short protoId, short modelIndex, short owner, short port, byte stateFlags,
        int astroId, int originAstroId, int dockIndex, int builderIndex, int level,
        double posX, double posY, double posZ,
        float rotX, float rotY, float rotZ, float rotW,
        float velX, float velY, float velZ,
        int hp, int hpMax, int hpRecover, int hpIncoming,
        float animationTime = 0, float prepareLength = 0, float workingLength = 0,
        uint animationState = 0, float animationPower = 0,
        int builderSp = 0, int builderSpMax = 0, int productSp = 0, int productSpMax = 0,
        int matter = 0, int matterMax = 0, int matterChange = 0,
        int energy = 0, int energyMax = 0, int energyChange = 0, byte unitBehavior = 0,
        byte turretState = 0, bool turretTargetInShootRange = false,
        long sampleTick = 0, int unitStateTick = 0, float unitDisturb = 0, byte[] nativeUnit = null,
        float unitAnimation = 0, float unitSteering = 0, float unitSpeed = 0, int builderState = 0, byte[] nativeTurret = null,
        SpaceEnemyActivity activity = default, EnemyUnitComponent? decodedNativeUnit = null)
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
        DockIndex = dockIndex;
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
        AnimationTime = animationTime;
        PrepareLength = prepareLength;
        WorkingLength = workingLength;
        AnimationState = animationState;
        AnimationPower = animationPower;
        BuilderSp = builderSp;
        BuilderSpMax = builderSpMax;
        ProductSp = productSp;
        ProductSpMax = productSpMax;
        Matter = matter;
        MatterMax = matterMax;
        MatterChange = matterChange;
        Energy = energy;
        EnergyMax = energyMax;
        EnergyChange = energyChange;
        UnitBehavior = unitBehavior;
        TurretState = turretState;
        TurretTargetInShootRange = turretTargetInShootRange;
        SampleTick = sampleTick;
        UnitStateTick = unitStateTick;
        NativeUnit = nativeUnit;
        DecodedNativeUnit = decodedNativeUnit;
        BuilderState = builderState; NativeTurret = nativeTurret;
        UnitAnimation = unitAnimation; UnitSteering = unitSteering; UnitSpeed = unitSpeed;
        UnitDisturb = unitDisturb;
        Activity = activity;
    }

    internal SpaceEnemyState(in SpaceEnemyState source, byte[] nativeUnit, in EnemyUnitComponent unit)
    {
        this = source; NativeUnit = nativeUnit; DecodedNativeUnit = unit;
    }

    public SpaceEnemyKind Kind { get; }
    public bool HasCombatStat { get; }
    public bool IsDynamic { get; }
    public short ProtoId { get; }
    public short ModelIndex { get; }
    public short Owner { get; }
    public short Port { get; }
    public byte StateFlags { get; }
    public int AstroId { get; }
    public int OriginAstroId { get; }
    public int DockIndex { get; }
    public int BuilderIndex { get; }
    public int Level { get; }
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
    public float AnimationTime { get; }
    public float PrepareLength { get; }
    public float WorkingLength { get; }
    public uint AnimationState { get; }
    public float AnimationPower { get; }
    public int BuilderSp { get; }
    public int BuilderSpMax { get; }
    public int ProductSp { get; }
    public int ProductSpMax { get; }
    public int Matter { get; }
    public int MatterMax { get; }
    public int MatterChange { get; }
    public int Energy { get; }
    public int EnergyMax { get; }
    public int EnergyChange { get; }

    /// <summary>
    /// The unit's vanilla <c>EnemyUnitComponent.behavior</c> (the <c>EEnemyBehavior</c> value), so the
    /// client's brief panel can print the real state line ("攻击中"/"巡逻中"/"回归中") instead of the
    /// default. Zero for every non-unit enemy. The byte is part of the current wire format, so a record
    /// without it is refused rather than silently defaulted. Native motion uses the same behavior.
    /// </summary>
    public byte UnitBehavior { get; }

    /// <summary>
    /// The turret's vanilla <c>DFSTurretComponent.state</c> (the <c>EDFTurretState</c> value) and
    /// <c>targetInShootRange</c>, so the client's brief panel prints "交火中" for an engaged hive turret
    /// instead of the default "警戒中" a component-less shell would show. Zero/false for every
    /// non-turret enemy; the in-range flag rides in the flags byte.
    /// </summary>
    public byte TurretState { get; }
    public bool TurretTargetInShootRange { get; }
    public long SampleTick { get; }
    public int UnitStateTick { get; }
    public byte[] NativeUnit { get; }
    public EnemyUnitComponent? DecodedNativeUnit { get; }
    public int BuilderState { get; }
    public byte[] NativeTurret { get; }
    public float UnitAnimation { get; }
    public float UnitSteering { get; }
    public float UnitSpeed { get; }
    public float UnitDisturb { get; }
    public SpaceEnemyActivity Activity { get; }
}

/// <summary>Encoding of <see cref="SpaceEnemyState"/> into one bounded state blob.</summary>
internal static class SpaceEnemyStateCodec
{
    private const byte HasCombatFlag = 1;
    private const byte IsDynamicFlag = 2;
    private const byte TurretInRangeFlag = 4;

    /// <summary>
    /// The largest vanilla <c>EEnemyBehavior</c> value; the wire carries it as one byte and rejects
    /// anything above, so a corrupt record cannot reach the panel. NebulaeModel deliberately does not
    /// reference the game assembly, so the value is pinned here with its vanilla name.
    /// </summary>
    public const byte MaxUnitBehavior = 9; // EEnemyBehavior.Engage

    /// <summary>The largest vanilla <c>EDFTurretState</c> value, pinned the same way.</summary>
    public const byte MaxTurretState = 2; // EDFTurretState.Aiming

    /// <summary>Base record size. Unit records append a native motion clock and disturbance input.</summary>
    public const int FixedSize = 4 + 1 + 1 + 1 + 8 + 20 + 52 + 16 + 20 + 40 + 1 + 1 + 4;

    public static bool TryEncode(in SpaceEnemyState state, out byte[] data) => TryEncodeCore(state, false, out data);

    // Only this path can skip a byte round-trip: it validates the captured struct and
    // creates its own fresh native buffer. Raw caller-supplied byte arrays remain fully checked.
    internal static bool TryEncodeCaptured(in SpaceEnemyState state, in EnemyUnitComponent unit,
        out byte[] data, out SpaceEnemyState canonical)
    {
        data = null; canonical = state;
        if (state.Kind != SpaceEnemyKind.SpaceUnit) return TryEncode(state, out data);
        if (!EnemyUnitStateCodec.IsValid(unit)) return false;
        canonical = new SpaceEnemyState(state, EnemyUnitStateCodec.Encode(unit), unit);
        return TryEncodeCore(canonical, true, out data);
    }

    private static bool TryEncodeCore(in SpaceEnemyState state, bool capturedNative, out byte[] data)
    {
        data = null;
        if (!SpaceEnemyKinds.IsDefined(state.Kind) || !state.Activity.Valid(state.Kind) || state.SampleTick < 0 ||
            float.IsNaN(state.UnitDisturb) || float.IsInfinity(state.UnitDisturb)) return false;
        if (state.BuilderSp < 0 || state.BuilderSpMax < 0 || state.BuilderSp > state.BuilderSpMax ||
            state.ProductSp < 0 || state.ProductSpMax < 0 || state.ProductSp > state.ProductSpMax ||
            state.MatterMax < 0 || state.EnergyMax < 0 || state.UnitBehavior > MaxUnitBehavior ||
            state.TurretState > MaxTurretState) return false;
        var flags = (byte)((state.HasCombatStat ? HasCombatFlag : 0) | (state.IsDynamic ? IsDynamicFlag : 0) |
            (state.TurretTargetInShootRange ? TurretInRangeFlag : 0));
        var nativeUnitBytes = state.Kind == SpaceEnemyKind.SpaceUnit ? state.NativeUnit ?? Array.Empty<byte>() : Array.Empty<byte>();
        var turret = state.NativeTurret ?? Array.Empty<byte>();
        if (nativeUnitBytes.Length > EnemyUnitStateCodec.MaxBytes || turret.Length > 512) return false;
        var writer = new AuthorityPayloadWriter(SizeFor(state.Kind) + nativeUnitBytes.Length + turret.Length);
        writer.WriteByte((byte)state.Kind);
        writer.WriteByte(flags);
        writer.WriteByte(state.StateFlags);
        writer.WriteUShort(unchecked((ushort)state.ProtoId));
        writer.WriteUShort(unchecked((ushort)state.ModelIndex));
        writer.WriteUShort(unchecked((ushort)state.Owner));
        writer.WriteUShort(unchecked((ushort)state.Port));
        writer.WriteInt(state.AstroId);
        writer.WriteInt(state.OriginAstroId);
        writer.WriteInt(state.DockIndex);
        writer.WriteInt(state.BuilderIndex);
        writer.WriteInt(state.Level);
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
        writer.WriteInt(WireBits.FromSingle(state.AnimationTime));
        writer.WriteInt(WireBits.FromSingle(state.PrepareLength));
        writer.WriteInt(WireBits.FromSingle(state.WorkingLength));
        writer.WriteInt(unchecked((int)state.AnimationState));
        writer.WriteInt(state.BuilderState);
        writer.WriteInt(WireBits.FromSingle(state.AnimationPower));
        writer.WriteInt(state.BuilderSp); writer.WriteInt(state.BuilderSpMax);
        writer.WriteInt(state.ProductSp); writer.WriteInt(state.ProductSpMax);
        writer.WriteInt(state.Matter); writer.WriteInt(state.MatterMax); writer.WriteInt(state.MatterChange);
        writer.WriteInt(state.Energy); writer.WriteInt(state.EnergyMax); writer.WriteInt(state.EnergyChange);
        writer.WriteByte(state.UnitBehavior);
        writer.WriteByte(state.TurretState);
        if (state.Kind == SpaceEnemyKind.SpaceUnit)
        {
            writer.WriteLong(state.SampleTick);
            writer.WriteInt(state.UnitStateTick);
            writer.WriteInt(WireBits.FromSingle(state.UnitDisturb));
            writer.WriteInt(WireBits.FromSingle(state.UnitAnimation));
            writer.WriteInt(WireBits.FromSingle(state.UnitSteering));
            writer.WriteInt(WireBits.FromSingle(state.UnitSpeed));
            var native = nativeUnitBytes;
            if (native.Length > 0 && !capturedNative && !EnemyUnitStateCodec.TryDecode(native, out _)) return false;
            if (!writer.TryWriteBytes(native, EnemyUnitStateCodec.MaxBytes)) return false;
        }
        state.Activity.Write(writer, state.Kind);
        if (!EnemyTurretStateCodec.Valid(turret, true) || !writer.TryWriteBytes(turret, 512)) return false;
        data = writer.Finish();
        if (data.Length != SizeFor(state.Kind) + (state.NativeUnit?.Length ?? 0) + turret.Length || data.Length > AuthorityLimits.StateRecordMaxBytes) return false;
        AuthorityStateFields.RememberEncoded(PoolKind.SpaceEnemy, data);
        return true;
    }

    public static int SizeFor(SpaceEnemyKind kind) => FixedSize + (kind == SpaceEnemyKind.SpaceUnit ? 32 : 0) + SpaceEnemyActivity.SizeFor(kind);

    public static bool TryDecode(byte[] source, int offset, int length, out SpaceEnemyState state,
        out AuthorityReject reject)
    {
        state = default;
        reject = AuthorityReject.Accepted;
        if (!AuthorityPayloadReader.TryCreate(source, offset, length, out var reader, out reject)) return false;
        if (!reader.TryReadByte(out var kindRaw) || !SpaceEnemyKinds.IsDefined((SpaceEnemyKind)kindRaw))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "space enemy kind=" + kindRaw);
            return false;
        }
        if (!reader.TryReadByte(out var flags))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing space enemy flags");
            return false;
        }
        if ((flags & ~(HasCombatFlag | IsDynamicFlag | TurretInRangeFlag)) != 0)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "space enemy flags=" + flags);
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
            !reader.TryReadInt(out var dockIndex) || !reader.TryReadInt(out var builderIndex) ||
            !reader.TryReadInt(out var level))
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
        int animationTime = 0, prepareLength = 0, workingLength = 0, animationState = 0, animationPower = 0, builderState = 0;
        if (!reader.TryReadInt(out animationTime) || !reader.TryReadInt(out prepareLength) ||
            !reader.TryReadInt(out workingLength) || !reader.TryReadInt(out animationState) || !reader.TryReadInt(out builderState) ||
            !reader.TryReadInt(out animationPower))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated animation");
            return false;
        }
        int builderSp = 0, builderSpMax = 0, productSp = 0, productSpMax = 0,
            matter = 0, matterMax = 0, matterChange = 0, energy = 0, energyMax = 0, energyChange = 0;
        if (!reader.TryReadInt(out builderSp) || !reader.TryReadInt(out builderSpMax) ||
            !reader.TryReadInt(out productSp) || !reader.TryReadInt(out productSpMax) ||
            !reader.TryReadInt(out matter) || !reader.TryReadInt(out matterMax) || !reader.TryReadInt(out matterChange) ||
            !reader.TryReadInt(out energy) || !reader.TryReadInt(out energyMax) || !reader.TryReadInt(out energyChange) ||
            builderSp < 0 || builderSpMax < 0 || builderSp > builderSpMax ||
            productSp < 0 || productSpMax < 0 || productSp > productSpMax ||
            matterMax < 0 || energyMax < 0)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid building presentation");
            return false;
        }
        byte unitBehavior = 0;
        if (!reader.TryReadByte(out unitBehavior) || unitBehavior > MaxUnitBehavior)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid unit behavior");
            return false;
        }
        byte turretState = 0;
        if (!reader.TryReadByte(out turretState) || turretState > MaxTurretState)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid turret state");
            return false;
        }
        long sampleTick = 0;
        int unitStateTick = 0, unitDisturbBits = 0, unitAnimationBits = 0, unitSteeringBits = 0, unitSpeedBits = 0;
        if ((SpaceEnemyKind)kindRaw == SpaceEnemyKind.SpaceUnit &&
            (!reader.TryReadLong(out sampleTick) || sampleTick < 0 || !reader.TryReadInt(out unitStateTick) ||
             !reader.TryReadInt(out unitDisturbBits) || !reader.TryReadInt(out unitAnimationBits) ||
             !reader.TryReadInt(out unitSteeringBits) || !reader.TryReadInt(out unitSpeedBits)))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid space motion anchor");
            return false;
        }
        var unitDisturb = WireBits.ToSingle(unitDisturbBits);
        var unitAnimation = WireBits.ToSingle(unitAnimationBits);
        var unitSteering = WireBits.ToSingle(unitSteeringBits);
        var unitSpeed = WireBits.ToSingle(unitSpeedBits);
        if (float.IsNaN(unitDisturb) || float.IsInfinity(unitDisturb) ||
            float.IsNaN(unitAnimation) || float.IsInfinity(unitAnimation) ||
            float.IsNaN(unitSteering) || float.IsInfinity(unitSteering) ||
            float.IsNaN(unitSpeed) || float.IsInfinity(unitSpeed))
        { reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid disturbance"); return false; }
        byte[] nativeUnit = Array.Empty<byte>();
        EnemyUnitComponent nativeComponent = default;
        if ((SpaceEnemyKind)kindRaw == SpaceEnemyKind.SpaceUnit &&
            (!reader.TryReadBytes(EnemyUnitStateCodec.MaxBytes, out nativeUnit) ||
                nativeUnit.Length > 0 && !EnemyUnitStateCodec.TryDecode(nativeUnit, out nativeComponent)))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid native space unit");
            return false;
        }
        if (!SpaceEnemyActivity.TryRead(ref reader, (SpaceEnemyKind)kindRaw, out var activity))
        { reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid space enemy activity"); return false; }
        if (!reader.TryReadBytes(512, out var nativeTurret) || !EnemyTurretStateCodec.Valid(nativeTurret, true))
        { reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid native turret"); return false; }
        if (!reader.EndOfPayload)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "trailing bytes");
            return false;
        }
        state = new SpaceEnemyState(
            (SpaceEnemyKind)kindRaw, (flags & HasCombatFlag) != 0, (flags & IsDynamicFlag) != 0,
            unchecked((short)protoRaw), unchecked((short)modelRaw),
            unchecked((short)ownerRaw), unchecked((short)portRaw), stateFlags,
            astroId, originAstroId, dockIndex, builderIndex, level,
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
            hp, hpMax, hpRecover, hpIncoming,
            WireBits.ToSingle(animationTime),
            WireBits.ToSingle(prepareLength),
            WireBits.ToSingle(workingLength),
            unchecked((uint)animationState),
            WireBits.ToSingle(animationPower),
            builderSp, builderSpMax, productSp, productSpMax,
            matter, matterMax, matterChange, energy, energyMax, energyChange, unitBehavior,
            turretState, (flags & TurretInRangeFlag) != 0, sampleTick, unitStateTick, unitDisturb, nativeUnit,
            unitAnimation, unitSteering, unitSpeed, builderState, nativeTurret, activity,
            nativeUnit.Length > 0 ? nativeComponent : (EnemyUnitComponent?)null);
        return true;
    }
}
