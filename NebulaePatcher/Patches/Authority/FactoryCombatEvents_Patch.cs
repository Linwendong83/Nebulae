using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority.Adapters;

namespace NebulaePatcher.Patches.Authority;

/// <summary>Report native commits; calculation and mutation stay in the original game.</summary>
[HarmonyPatch]
internal static class FactoryCombatEvents_Patch
{
    private static FactoryCombatSnapshotAdapter Source => Multiplayer.IsActive && Multiplayer.Session.IsServer
        ? Multiplayer.Session.AuthorityRuntime?.FactoryCombatHost : null;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.AddEntityData))]
    private static void Added(PlanetFactory __instance, int __result) => Source?.Added(__instance, __result);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.RemoveEntityWithComponents))]
    private static void BeforeRemove(PlanetFactory __instance, int id, out ObjectKey __state)
    {
        __state = default;
        Source?.TryGetEntityKey(__instance, id, out __state);
    }

    [HarmonyPostfix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.RemoveEntityWithComponents))]
    private static void Removed(PlanetFactory __instance, int id, ObjectKey __state)
    {
        if (__state.IsValid && (id >= __instance.entityCursor || __instance.entityPool[id].id != id)) Source?.Removed(__state);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CombatStat), nameof(CombatStat.TickSkillLogic))]
    private static void BeforeCombatTick(ref CombatStat __instance, out (int Planet, int Entity) __state)
        => __state = __instance.objectType == (int)EObjectType.Entity ? (__instance.originAstroId, __instance.objectId) : default;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CombatStat), nameof(CombatStat.TickSkillLogic))]
    private static void CombatTick((int Planet, int Entity) __state) => Source?.MarkState(__state.Planet, __state.Entity);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ConstructStat), nameof(ConstructStat.GameTick))]
    private static void BeforeConstructTick(ref ConstructStat __instance, out int __state) => __state = __instance.entityId;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ConstructStat), nameof(ConstructStat.GameTick))]
    private static void ConstructTick(PlanetFactory factory, int __state) => Source?.MarkState(factory.planetId, __state);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ConstructionSystem), nameof(ConstructionSystem.AddConstructStat))]
    private static void ConstructCreated(ConstructionSystem __instance, int entityId) => Source?.MarkState(__instance.factory.planetId, entityId);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SkillSystem), nameof(SkillSystem.DamageObject))]
    private static void Damage(ref SkillTarget target)
    {
        if (target.type == ETargetType.None && SpaceSector.IsPlanetAstro(target.astroId)) Source?.MarkState(target.astroId, target.id);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SkillSystem), nameof(SkillSystem.DamageGroundObjectByLocalCaster))]
    private static void LocalDamage(PlanetFactory factory, ref SkillTargetLocal target)
    {
        if (target.type == ETargetType.None) Source?.MarkState(factory.planetId, target.id);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SkillSystem), nameof(SkillSystem.DamageGroundObjectByRemoteCaster))]
    private static void RemoteDamage(PlanetFactory factory, ref SkillTargetLocal target)
    {
        if (target.type == ETargetType.None) Source?.MarkState(factory.planetId, target.id);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SkillSystem), nameof(SkillSystem.AddCombatStatHPIncoming))]
    private static void Incoming(ref SkillTarget obj)
    {
        if (obj.type == ETargetType.None && SpaceSector.IsPlanetAstro(obj.astroId)) Source?.MarkState(obj.astroId, obj.id);
    }
}
