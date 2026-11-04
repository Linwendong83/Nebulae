using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Authority.Adapters;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class DarkFogActivity_Patch
{
    [HarmonyPrefix, HarmonyPatch(typeof(SectorModel), nameof(SectorModel.DrawInstancedBatches))]
    internal static void Draw(SectorModel __instance)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient) SpaceEnemyPresentation.PrepareDraw(__instance, false);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(SectorModel), "UpdateGlobalShaderVars")]
    internal static void UpdateAlert(SectorModel __instance)
    {
        if (Multiplayer.IsActive && !Multiplayer.IsDedicated) HiveSensor.UpdateAlert(__instance.sector);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(SectorModel), nameof(SectorModel.DrawStarmapBatches))]
    internal static void DrawStarmap(SectorModel __instance)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient) SpaceEnemyPresentation.PrepareDraw(__instance, true);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(EnemyDFGroundSystem), nameof(EnemyDFGroundSystem.IsBasePit))]
    internal static bool IsBasePit(EnemyDFGroundSystem __instance, int baseId, ref bool __result)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient) return true;
        __result = Multiplayer.Session.AuthorityRuntime.DarkFogReplica.BaseAlive.TryGetValue((__instance.factory.planetId, baseId), out var alive) && !alive;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(EnemyDFGroundSystem), nameof(EnemyDFGroundSystem.CheckBaseCanRemoved))]
    internal static bool CanRemove(EnemyDFGroundSystem __instance, int baseId, ref int __result)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient) return true;
        var replica = Multiplayer.Session.AuthorityRuntime.DarkFogReplica;
        var identity = (__instance.factory.planetId, baseId);
        __result = replica.BaseRemovalFlags.TryGetValue(identity, out var flags) ? flags : 1;
        // A pit holds no garrison. The host releases those formation tags while it samples the scope
        // (DarkFogBasePitCleanup); a client still holding the older record must not keep a verdict
        // that nothing on this peer can clear, or the pit stays unremovable and a geothermal station
        // aimed at it keeps reporting the generic collision. Only the units bit is derived here —
        // the core and building bits stay host facts.
        if ((__result & 4) != 0 && (!replica.BaseAlive.TryGetValue(identity, out var alive) || !alive))
            __result = HasLiveUnits(__instance.factory, baseId) ? __result | 4 : __result & ~4;
        return false;
    }

    /// <summary>True while the base still has a unit on the field that a player can actually kill.</summary>
    private static bool HasLiveUnits(PlanetFactory factory, int baseId)
    {
        var pool = factory.enemyPool;
        for (var id = 1; id < factory.enemyCursor && id < pool.Length; id++)
            if (pool[id].id == id && pool[id].owner == baseId && (pool[id].unitId > 0 || pool[id].dynamic)) return true;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(DFGBaseComponent), nameof(DFGBaseComponent.GetRelay))]
    internal static bool GetRelay(DFGBaseComponent __instance, ref DFRelayComponent __result)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient) return true;
        __result = FindRelay(GameMain.spaceSector, __instance); return false;
    }

    internal static DFRelayComponent FindRelay(SpaceSector sector, DFGBaseComponent item)
    {
        if (sector == null || item.relayEnemyId <= 0 || item.relayEnemyId >= sector.enemyCursor) return null;
        ref var enemy = ref sector.enemyPool[item.relayEnemyId];
        var hive = sector.GetHiveByAstroId(item.hiveAstroId);
        if (enemy.id != item.relayEnemyId || enemy.originAstroId != item.hiveAstroId || hive == null ||
            enemy.dfRelayId <= 0 || enemy.dfRelayId >= hive.relays.cursor) return null;
        var relay = hive.relays.buffer[enemy.dfRelayId];
        return relay?.enemyId == item.relayEnemyId && relay.baseId == item.id && relay.targetAstroId == item.groundSystem.factory.planetId ? relay : null;
    }
}
