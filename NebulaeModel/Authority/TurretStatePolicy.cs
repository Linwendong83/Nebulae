using System;
using NebulaeModel.Packets.Authority;

namespace NebulaeModel.Authority;

/// <summary>Configuration changes publish immediately; native simulation is corrected at checkpoints.</summary>
internal static class TurretStatePolicy
{
    public const int CheckpointIntervalTicks = 120;

    public static bool SameCommittedFacts(in TurretState a, in TurretState b) =>
        a.EntityId == b.EntityId && a.ModelIndex == b.ModelIndex && a.Type == b.Type &&
        a.Group == b.Group && a.VsSettings == b.VsSettings && a.PhasePos == b.PhasePos &&
        a.TargetId == b.TargetId && a.TargetAstro == b.TargetAstro && a.TargetType == b.TargetType && a.TargetGeneration == b.TargetGeneration &&
        (a.Flags & 4) == (b.Flags & 4) &&
        a.ItemId == b.ItemId && a.ItemCount == b.ItemCount && a.ItemInc == b.ItemInc &&
        a.BulletCount == b.BulletCount && a.ItemBulletCount == b.ItemBulletCount &&
        a.BulletDamage == b.BulletDamage && a.BulletInc == b.BulletInc &&
        a.KillCount == b.KillCount && a.TotalDamage == b.TotalDamage;

    public static bool ShouldPublish(in TurretState previous, in TurretState current)
    {
        if (previous.EntityId != current.EntityId || previous.ModelIndex != current.ModelIndex ||
            previous.Type != current.Type || previous.Group != current.Group || previous.VsSettings != current.VsSettings ||
            previous.PhasePos != current.PhasePos || (previous.Flags & 4) != (current.Flags & 4) ||
            Math.Sign(previous.SupernovaTick) != Math.Sign(current.SupernovaTick) ||
            current.SampleTick < previous.SampleTick) return true;

        // Countdown progression carries no new decision. A restart or a change of phase does.
        if (current.SupernovaTick != 0 &&
            previous.SampleTick + Math.Abs((int)previous.SupernovaTick) !=
            current.SampleTick + Math.Abs((int)current.SupernovaTick)) return true;

        // Native belt intake, ammo consumption, target search and firing need no per-shot records.
        // A settled turret still gets a checkpoint if its native inventory differs from the last
        // one, so an empty belt or a different local target cannot leave resources stale forever.
        var moving = current.TargetId != 0 || current.SupernovaTick != 0 ||
            (current.Direction - previous.Direction).sqrMagnitude > 0.0001f;
        return (moving || !SameCommittedFacts(previous, current)) &&
            current.SampleTick - previous.SampleTick >= CheckpointIntervalTicks;
    }
}
