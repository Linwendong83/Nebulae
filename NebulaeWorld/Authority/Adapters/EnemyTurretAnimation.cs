#region

using UnityEngine;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>
/// The turret aiming animation the vanilla ground and hive turret ticks write into the enemy
/// animation pool (<c>prepare_length/working_length/power = DFGTurretComponent.localDir</c>, plus the
/// fire-cycle <c>time</c>). Both vanilla copies are gated — the ground one on
/// <c>EnemyDFGroundSystem.isLocalLoaded</c> (the host standing on that planet), the hive one on
/// <c>isLocal</c> — so a host that simulates a planet it does not render never fills the pool, and
/// every client shell (which has no turret component at all) sees a frozen barrel.
/// </summary>
/// <remarks>
/// The turret AI itself runs for every factory on the host, so the component's <c>localDir</c>,
/// <c>state</c>, <c>fire</c> and <c>heat</c> are always fresh. Recomputing the same AnimData from the
/// component gives clients the aiming animation vanilla would have shown, at the replication cadence
/// the enemy state already travels at. The formula mirrors <c>EnemyDFGroundSystem.GameTickLogic_Turret</c>
/// and the hive equivalent, with two guards vanilla does not need: a non-positive attack interval or
/// sensor span cannot produce NaN on the wire.
/// </remarks>
internal static class EnemyTurretAnimation
{
    public static AnimData Resolve(Vector3 localDir, EDFTurretState state, int fire, int heat,
        float sensorRange, float attackRange, float attackInterval)
    {
        var anim = new AnimData
        {
            prepare_length = localDir.x,
            working_length = localDir.y,
            power = localDir.z,
            state = (uint)state
        };
        var cycle = 0f;
        if (attackInterval > 0f)
        {
            cycle = (float)(-fire) / attackInterval;
            if (float.IsNaN(cycle)) cycle = 0f;
            else if (cycle < -1f) cycle = -1f;
        }
        var heatFactor = 2f;
        if (heat < 0) heatFactor = Mathf.Clamp01((float)heat / 3600f + 1f);
        else if (heat > 0) heatFactor = 0f;
        var rangeFactor = 0f;
        if (state == EDFTurretState.Aiming && cycle >= 0f)
        {
            var span = sensorRange - attackRange;
            if (span > 0f)
            {
                var reach = Mathf.Sqrt(localDir.x * localDir.x + localDir.y * localDir.y + localDir.z * localDir.z);
                rangeFactor = Mathf.Clamp01((sensorRange - reach) / span);
            }
        }
        if (cycle >= 0f) cycle += heatFactor < 2f ? Mathf.Min(heatFactor, rangeFactor) : rangeFactor;
        anim.time = cycle + 10f;
        return anim;
    }

    /// <summary>
    /// Vanilla refreshes a turret's aim animation only while its growth slot's builder is alive
    /// (<c>builders.buffer[turret.builderId].state &gt; 0</c>). A turret with no resolvable builder has
    /// nothing to freeze it, so it still aims — and still gets its animation.
    /// </summary>
    public static bool BuilderIsActive(DataPool<EnemyBuilderComponent> builders, int builderId) =>
        builderId <= 0 || builderId >= builders.cursor ||
        builders.buffer[builderId].id != builderId || builders.buffer[builderId].state > 0;

    public static float AttackInterval(int modelIndex)
    {
        var prefabs = SpaceSector.PrefabDescByModelIndex;
        return modelIndex > 0 && modelIndex < prefabs.Length ? prefabs[modelIndex]?.dfTurretAttackInterval ?? 0f : 0f;
    }
}
