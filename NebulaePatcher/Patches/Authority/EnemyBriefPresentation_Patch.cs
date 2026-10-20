using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(EnemyBriefInfo), nameof(EnemyBriefInfo.SetBriefInfo))]
internal static class EnemyBriefPresentation_Patch
{
    [HarmonyPostfix]
    public static void Postfix(EnemyBriefInfo __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer || __instance.enemyId <= 0 || __instance.astroId <= 0) return;
        var replica = Multiplayer.Session.AuthorityRuntime.WorldReplica;
        // A client shell carries no logic ids (its builderId is zero), so vanilla reads no builder
        // state and the panel would divide 0/0. Hive enemies live in the sector pool, ground enemies
        // in their planet's pool; the host publishes each one's building presentation for this panel.
        if (__instance.astroId > 1000000)
        {
            if (TryReadSpaceState(replica, __instance, out var spaceState)) Apply(__instance, spaceState);
        }
        else if (TryReadGroundState(replica, __instance, out var groundState))
        {
            Apply(__instance, groundState);
        }
        // Whatever the data path decided, the vanilla panel divides sp/spMax ("建造中") and
        // productSp/productSpMax ("制造中") without a zero guard — including while no mirror has
        // arrived yet, so a shell alone must never reach the division as 0/0.
        Guard(__instance);
    }

    private static bool TryReadGroundState(ClientWorldReplica replica, EnemyBriefInfo info, out GroundEnemyState state)
    {
        state = default;
        return replica.TryFindKey(PoolKind.GroundEnemy, info.astroId, info.enemyId, out var key) &&
            replica.Mirrors.TryGetValue(key, out var mirror) && mirror.CanonicalState != null &&
            GroundEnemyStateCodec.TryDecode(mirror.CanonicalState, 0, mirror.CanonicalState.Length, out state, out _);
    }

    private static bool TryReadSpaceState(ClientWorldReplica replica, EnemyBriefInfo info, out SpaceEnemyState state)
    {
        state = default;
        return replica.TryFindKey(PoolKind.SpaceEnemy, AuthorityScope.Sector, info.enemyId, out var key) &&
            replica.Mirrors.TryGetValue(key, out var mirror) && mirror.CanonicalState != null &&
            SpaceEnemyStateCodec.TryDecode(mirror.CanonicalState, 0, mirror.CanonicalState.Length, out state, out _);
    }

    public static void Apply(EnemyBriefInfo info, GroundEnemyState state)
    {
        info.builderState = state.BuilderState;
        info.sp = state.BuilderSp; info.spMax = state.BuilderSpMax;
        info.matter = state.Matter; info.matterMax = state.MatterMax; info.matterChange = state.MatterChange;
        info.energy = state.Energy; info.energyMax = state.EnergyMax; info.energyChange = state.EnergyChange;
        info.productSp = state.ProductSp; info.productSpMax = state.ProductSpMax;
        // A shell carries no turret component, so vanilla's SetBriefInfo left these at Reset()'s zero
        // and the panel printed "警戒中" forever. The host publishes the real turret state instead.
        info.turretState = state.TurretState;
        info.turretTargetInShootRange = state.TurretTargetInShootRange;
    }

    public static void Apply(EnemyBriefInfo info, SpaceEnemyState state)
    {
        info.relayCount = state.Activity.RelayCount; info.tinderCount = state.Activity.TinderCount;
        info.buildRelaySp = state.Activity.RelaySp; info.buildRelaySpMax = state.Activity.RelaySpMax;
        info.buildTinderSp = state.Activity.TinderSp; info.buildTinderSpMax = state.Activity.TinderSpMax;
        info.builderState = state.BuilderState;
        info.sp = state.BuilderSp; info.spMax = state.BuilderSpMax;
        info.productSp = state.ProductSp; info.productSpMax = state.ProductSpMax;
        info.matter = state.Matter; info.matterMax = state.MatterMax; info.matterChange = state.MatterChange;
        info.energy = state.Energy; info.energyMax = state.EnergyMax; info.energyChange = state.EnergyChange;
        info.turretState = state.TurretState;
        info.turretTargetInShootRange = state.TurretTargetInShootRange;
    }

    /// <summary>
    /// Keeps the vanilla divisions finite: a zeroed spMax (a shell, a not-yet-arrived mirror, or a
    /// degenerate pattern) renders as 0.0% instead of NaN. Non-builder enemies never display these
    /// fields, so the sentinel is invisible to them.
    /// </summary>
    public static void Guard(EnemyBriefInfo info)
    {
        if (info.spMax <= 0) { info.sp = 0; info.spMax = 1; }
        if (info.productSpMax <= 0) { info.productSp = 0; info.productSpMax = 1; }
    }
}
