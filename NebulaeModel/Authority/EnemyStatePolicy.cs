using NebulaeModel.Packets.Authority;

namespace NebulaeModel.Authority;

internal static class EnemyStatePolicy
{
    public const int CheckpointIntervalTicks = 120;
    // Active native units can accelerate/turn sharply between accepted target samples.
    // Bound that divergence without extrapolating their poses or changing native motion.
    public const int UnitCheckpointIntervalTicks = 6;

    public static bool ShouldPublish(in GroundEnemyState a, in GroundEnemyState b) =>
        a.Kind != b.Kind || a.ProtoId != b.ProtoId || a.ModelIndex != b.ModelIndex || a.Owner != b.Owner ||
        a.Port != b.Port || a.BaseId != b.BaseId || a.Level != b.Level || a.StateFlags != b.StateFlags ||
        a.AstroId != b.AstroId || a.OriginAstroId != b.OriginAstroId || a.IsDynamic != b.IsDynamic ||
        a.HasCombatStat != b.HasCombatStat || a.Hp != b.Hp || a.HpMax != b.HpMax ||
        a.HpRecover != b.HpRecover || a.HpIncoming != b.HpIncoming || a.UnitBehavior != b.UnitBehavior ||
        b.SampleTick < a.SampleTick || b.SampleTick - a.SampleTick >= UnitCheckpointIntervalTicks;

    public static bool ShouldPublish(in SpaceEnemyState a, in SpaceEnemyState b) =>
        a.Kind != b.Kind || a.ProtoId != b.ProtoId || a.ModelIndex != b.ModelIndex || a.Owner != b.Owner ||
        a.Port != b.Port || a.Level != b.Level || a.StateFlags != b.StateFlags ||
        a.AstroId != b.AstroId || a.OriginAstroId != b.OriginAstroId || a.IsDynamic != b.IsDynamic ||
        a.HasCombatStat != b.HasCombatStat || a.Hp != b.Hp || a.HpMax != b.HpMax ||
        a.HpRecover != b.HpRecover || a.HpIncoming != b.HpIncoming || a.UnitBehavior != b.UnitBehavior ||
        b.SampleTick < a.SampleTick || b.SampleTick - a.SampleTick >= UnitCheckpointIntervalTicks;

    /// <summary>
    /// Whether one reading of a ground enemy says anything its subscribers do not already hold.
    /// </summary>
    /// <param name="previous">The reading the last published record was built from.</param>
    /// <param name="decoded">The reading just taken from the host's pools.</param>
    /// <param name="movingUnit">
    /// True for a member whose pose the receiver recomputes from the captured native component.
    /// </param>
    /// <remarks>
    /// <para>
    /// This is the per-member gate the snapshot adapters apply before handing a record back, and it
    /// exists so the decision is made once instead of once per adapter. Its two branches answer the
    /// same question — "did a fact move?" — with the two predicates above, and the polarity is
    /// load-bearing in a way that is invisible when it is wrong:
    /// </para>
    /// <para>
    /// A moving unit keeps <see cref="ShouldPublish(in GroundEnemyState, in GroundEnemyState)"/>. Its
    /// pose drifts against the host's own anchor every tick, so a reading that differs only in pose
    /// must stay suppressed, and the checkpoint clause is what eventually re-anchors it.
    /// </para>
    /// <para>
    /// Every other member is fully authoritative — its record <em>is</em> the fact — so any
    /// difference at all must publish. That is exactly the negation of
    /// <see cref="SameState(in GroundEnemyState, in GroundEnemyState)"/>, which is the byte equality
    /// the replicator performs a moment later. Answering "unchanged" here for a record whose bytes
    /// moved is not a missed optimization: the adapter hands back the previous bytes, the replicator
    /// compares them against what it already sent, finds them equal, and the fact — a building's
    /// hitpoints, a base's evolve level, a turret's state — never reaches a subscriber at all.
    /// </para>
    /// </remarks>
    public static bool HasMoved(in GroundEnemyState previous, in GroundEnemyState decoded, bool movingUnit) =>
        movingUnit ? ShouldPublish(previous, decoded) : !SameState(previous, decoded);

    /// <summary>
    /// Whether one reading of a space enemy says anything its subscribers do not already hold.
    /// </summary>
    /// <remarks><inheritdoc cref="HasMoved(in GroundEnemyState, in GroundEnemyState, bool)"/></remarks>
    public static bool HasMoved(in SpaceEnemyState previous, in SpaceEnemyState decoded, bool movingUnit) =>
        movingUnit ? ShouldPublish(previous, decoded) : !SameState(previous, decoded);

    /// <summary>
    /// Whether two readings of one ground enemy carry exactly the same facts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the canonical-byte comparison the replicator already performs after encoding, moved
    /// in front of it. <see cref="GroundEnemyStateCodec.TryEncode"/> is a pure function of the state,
    /// so two states that agree on every field encode to identical bytes and two that disagree on any
    /// field do not. Deciding from the struct therefore costs no allocation and reaches the same
    /// verdict the byte comparison would have reached a few microseconds later.
    /// </para>
    /// <para>
    /// It deliberately covers <em>every</em> field, pose and animation included, because it is used
    /// for members whose record is fully authoritative. A moving unit's pose is the one exception:
    /// the receiver recomputes it from the native component, so those keep
    /// <see cref="ShouldPublish(in GroundEnemyState, in GroundEnemyState)"/> instead, where a pose
    /// change is not by itself a reason to publish.
    /// </para>
    /// </remarks>
    public static bool SameState(in GroundEnemyState a, in GroundEnemyState b) =>
        a.Kind == b.Kind && a.HasCombatStat == b.HasCombatStat && a.IsDynamic == b.IsDynamic &&
        a.ProtoId == b.ProtoId && a.ModelIndex == b.ModelIndex && a.Owner == b.Owner && a.Port == b.Port &&
        a.StateFlags == b.StateFlags && a.AstroId == b.AstroId && a.OriginAstroId == b.OriginAstroId &&
        a.BaseId == b.BaseId && a.BuilderIndex == b.BuilderIndex && a.Level == b.Level &&
        a.PosX == b.PosX && a.PosY == b.PosY && a.PosZ == b.PosZ &&
        a.RotX == b.RotX && a.RotY == b.RotY && a.RotZ == b.RotZ && a.RotW == b.RotW &&
        a.VelX == b.VelX && a.VelY == b.VelY && a.VelZ == b.VelZ &&
        a.Hp == b.Hp && a.HpMax == b.HpMax && a.HpRecover == b.HpRecover && a.HpIncoming == b.HpIncoming &&
        a.AnimationTime == b.AnimationTime && a.PrepareLength == b.PrepareLength &&
        a.WorkingLength == b.WorkingLength && a.AnimationState == b.AnimationState && a.BuilderState == b.BuilderState &&
        a.AnimationPower == b.AnimationPower &&
        a.UnitAnimation == b.UnitAnimation && a.UnitDisturb == b.UnitDisturb &&
        a.UnitSteering == b.UnitSteering && a.UnitSpeed == b.UnitSpeed &&
        a.BuilderSp == b.BuilderSp && a.BuilderSpMax == b.BuilderSpMax &&
        a.Matter == b.Matter && a.MatterMax == b.MatterMax &&
        a.Energy == b.Energy && a.EnergyMax == b.EnergyMax &&
        a.MatterChange == b.MatterChange && a.EnergyChange == b.EnergyChange &&
        a.ProductSp == b.ProductSp && a.ProductSpMax == b.ProductSpMax &&
        a.UnitBehavior == b.UnitBehavior && a.TurretState == b.TurretState &&
        a.TurretTargetInShootRange == b.TurretTargetInShootRange &&
        a.SampleTick == b.SampleTick && a.UnitStateTick == b.UnitStateTick &&
        SameNativeUnit(a.NativeUnit, b.NativeUnit) && SameNativeUnit(a.NativeTurret, b.NativeTurret);

    /// <summary>Whether two readings of one space enemy carry exactly the same facts.</summary>
    /// <remarks><inheritdoc cref="SameState(in GroundEnemyState, in GroundEnemyState)"/></remarks>
    public static bool SameState(in SpaceEnemyState a, in SpaceEnemyState b) =>
        a.Kind == b.Kind && a.HasCombatStat == b.HasCombatStat && a.IsDynamic == b.IsDynamic &&
        a.ProtoId == b.ProtoId && a.ModelIndex == b.ModelIndex && a.Owner == b.Owner && a.Port == b.Port &&
        a.StateFlags == b.StateFlags && a.AstroId == b.AstroId && a.OriginAstroId == b.OriginAstroId &&
        a.DockIndex == b.DockIndex && a.BuilderIndex == b.BuilderIndex && a.Level == b.Level &&
        a.PosX == b.PosX && a.PosY == b.PosY && a.PosZ == b.PosZ &&
        a.RotX == b.RotX && a.RotY == b.RotY && a.RotZ == b.RotZ && a.RotW == b.RotW &&
        a.VelX == b.VelX && a.VelY == b.VelY && a.VelZ == b.VelZ &&
        a.Hp == b.Hp && a.HpMax == b.HpMax && a.HpRecover == b.HpRecover && a.HpIncoming == b.HpIncoming &&
        a.AnimationTime == b.AnimationTime && a.PrepareLength == b.PrepareLength &&
        a.WorkingLength == b.WorkingLength && a.AnimationState == b.AnimationState && a.BuilderState == b.BuilderState &&
        a.AnimationPower == b.AnimationPower &&
        a.BuilderSp == b.BuilderSp && a.BuilderSpMax == b.BuilderSpMax &&
        a.ProductSp == b.ProductSp && a.ProductSpMax == b.ProductSpMax &&
        a.Matter == b.Matter && a.MatterMax == b.MatterMax && a.MatterChange == b.MatterChange &&
        a.Energy == b.Energy && a.EnergyMax == b.EnergyMax && a.EnergyChange == b.EnergyChange &&
        a.UnitBehavior == b.UnitBehavior && a.TurretState == b.TurretState &&
        a.TurretTargetInShootRange == b.TurretTargetInShootRange &&
        a.SampleTick == b.SampleTick && a.UnitStateTick == b.UnitStateTick &&
        a.UnitAnimation == b.UnitAnimation && a.UnitSteering == b.UnitSteering && a.UnitSpeed == b.UnitSpeed &&
        a.UnitDisturb == b.UnitDisturb && a.Activity.Same(b.Activity) &&
        SameNativeUnit(a.NativeUnit, b.NativeUnit) && SameNativeUnit(a.NativeTurret, b.NativeTurret);

    /// <summary>
    /// Content equality for the opaque native component blob. The host allocates a fresh array on
    /// every reading, so reference equality would report "changed" for an unchanged unit.
    /// </summary>
    private static bool SameNativeUnit(byte[] a, byte[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if ((a?.Length ?? 0) == 0 && (b?.Length ?? 0) == 0) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
