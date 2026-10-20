using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Authority.Adapters;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class DarkFogActivity_Patch
{
    [HarmonyPrefix, HarmonyPatch(typeof(SectorModel), nameof(SectorModel.DrawInstancedBatches))]
    internal static void Draw(SectorModel __instance)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient) SpaceEnemyPresentation.PrepareDraw(__instance, false);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(SectorModel), nameof(SectorModel.DrawStarmapBatches))]
    internal static void DrawStarmap(SectorModel __instance)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient) SpaceEnemyPresentation.PrepareDraw(__instance, true);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(EnemyDFGroundSystem), nameof(EnemyDFGroundSystem.IsBasePit))]
    internal static bool IsBasePit(EnemyDFGroundSystem __instance, int baseId, ref bool __result)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient ||
            !Multiplayer.Session.AuthorityRuntime.DarkFogReplica.BaseAlive.TryGetValue((__instance.factory.planetId, baseId), out var alive)) return true;
        __result = !alive; return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(EnemyDFGroundSystem), nameof(EnemyDFGroundSystem.CheckBaseCanRemoved))]
    internal static bool CanRemove(EnemyDFGroundSystem __instance, int baseId, ref int __result)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient ||
            !Multiplayer.Session.AuthorityRuntime.DarkFogReplica.BaseRemovalFlags.TryGetValue((__instance.factory.planetId, baseId), out var flags)) return true;
        __result = flags; return false;
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
