using NebulaeModel.Packets.Authority;

namespace NebulaeModel.Authority;

/// <summary>
/// Whether two readings of one base's presentation carry exactly the same facts.
/// </summary>
/// <remarks>
/// <para>
/// This is the canonical-byte comparison the replicator performs after encoding, moved in front of
/// it. <see cref="GroundBaseStateCodec.Encode"/> is a pure function of the state, so agreeing on
/// every field means identical bytes and disagreeing on any field means different bytes. Deciding
/// from the state therefore costs no allocation and reaches the same verdict the byte comparison
/// would have reached a few microseconds later.
/// </para>
/// <para>
/// It deliberately covers <em>every</em> field, including the evolve blob and the formation tags,
/// because the base summary is a presentation record the host restates in full: nothing in it is
/// recomputed by the receiver, so a moved fact must always publish.
/// </para>
/// </remarks>
internal static class GroundBaseStatePolicy
{
    public static bool SameState(GroundBaseState a, GroundBaseState b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        return a.EnemyId == b.EnemyId && a.RuinId == b.RuinId && a.Incoming == b.Incoming &&
            a.Buildings == b.Buildings && a.Units == b.Units && a.CoreAlive == b.CoreAlive &&
            a.RemovalFlags == b.RemovalFlags && a.HiveAstroId == b.HiveAstroId && a.RelayEnemyId == b.RelayEnemyId &&
            a.X == b.X && a.Y == b.Y && a.Z == b.Z &&
            SameBytes(a.Evolution, b.Evolution) && SameForms(a.Forms, b.Forms);
    }

    /// <summary>
    /// Content equality for the evolve blob. The host allocates a fresh array on every reading, so
    /// reference equality would report "changed" for an unchanged base.
    /// </summary>
    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Content equality for the three formation tag arrays, whose lengths are fixed by form.</summary>
    private static bool SameForms(byte[][] a, byte[][] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        for (var form = 0; form < a.Length; form++)
        {
            if (!SameBytes(a[form], b[form])) return false;
        }
        return true;
    }
}
