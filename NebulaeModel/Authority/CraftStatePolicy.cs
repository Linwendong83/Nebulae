using NebulaeModel.Packets.Authority;

namespace NebulaeModel.Authority;

internal static class CraftStatePolicy
{
    public const int CheckpointIntervalTicks = 120;
    public const int PlayerCheckpointIntervalTicks = 12;
    public static bool ShouldPublish(in CraftState a, in CraftState b) =>
        a.HasCombatStat != b.HasCombatStat || a.Hp != b.Hp || a.HpMax != b.HpMax || a.HpRecover != b.HpRecover ||
        a.HpIncoming != b.HpIncoming || a.ProtoId != b.ProtoId || a.ModelIndex != b.ModelIndex || a.Port != b.Port ||
        a.Owner != b.Owner || a.PlayerOwner != b.PlayerOwner || a.Prototype != b.Prototype ||
        a.IsDynamic != b.IsDynamic || a.StateFlags != b.StateFlags || a.AstroId != b.AstroId ||
        b.SampleTick < a.SampleTick || b.SampleTick - a.SampleTick >=
            (b.PlayerOwner != 0 ? PlayerCheckpointIntervalTicks : CheckpointIntervalTicks);

    /// <summary>
    /// Whether two readings of one craft carry exactly the same facts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The canonical-byte comparison the replicator already performs after encoding, moved in front of
    /// it. <see cref="CraftStateCodec.TryEncode"/> is a pure function of the state, so agreeing on
    /// every field means identical bytes and disagreeing on any field means different bytes. Deciding
    /// from the struct costs no allocation and reaches the same verdict the byte comparison would.
    /// </para>
    /// <para>
    /// It deliberately covers <em>every</em> field, pose included, because it is used for craft whose
    /// record is fully authoritative. A moving craft's pose is the exception: the receiver replays
    /// native steering from the captured inputs, so those keep
    /// <see cref="ShouldPublish(in CraftState, in CraftState)"/> instead, where a pose change is not
    /// by itself a reason to publish.
    /// </para>
    /// </remarks>
    public static bool SameState(in CraftState a, in CraftState b) =>
        a.HasCombatStat == b.HasCombatStat && a.IsDynamic == b.IsDynamic && a.IsSpace == b.IsSpace &&
        a.ProtoId == b.ProtoId && a.ModelIndex == b.ModelIndex && a.Port == b.Port &&
        a.Prototype == b.Prototype && a.StateFlags == b.StateFlags && a.AstroId == b.AstroId &&
        a.Owner == b.Owner && a.PlayerOwner == b.PlayerOwner &&
        a.PosX == b.PosX && a.PosY == b.PosY && a.PosZ == b.PosZ &&
        a.RotX == b.RotX && a.RotY == b.RotY && a.RotZ == b.RotZ && a.RotW == b.RotW &&
        a.VelX == b.VelX && a.VelY == b.VelY && a.VelZ == b.VelZ &&
        a.Hp == b.Hp && a.HpMax == b.HpMax && a.HpRecover == b.HpRecover && a.HpIncoming == b.HpIncoming &&
        a.SampleTick == b.SampleTick && SameNative(a.NativeFleet, b.NativeFleet) && SameNative(a.NativeUnit, b.NativeUnit);

    /// <summary>
    /// Content equality for the opaque native blob. The host allocates a fresh array on every reading,
    /// so reference equality would report "changed" for an unchanged component.
    /// </summary>
    private static bool SameNative(byte[] a, byte[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
