using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class EnemyCreatedReplication_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods() => new[] { typeof(PlanetFactory), typeof(SpaceSector) }
        .SelectMany(type => type.GetMethods().Where(method => method.Name == "CreateEnemyFinal" && method.ReturnType == typeof(int)));
    [HarmonyPostfix]
    internal static void Postfix(object __instance, int __result)
    {
        if (Multiplayer.Session?.IsServer != true || __result <= 0) return;
        var runtime = Multiplayer.Session.AuthorityRuntime;
        if (__instance is PlanetFactory factory) runtime?.GroundEnemyHost?.Added(factory, __result);
        else runtime?.SpaceEnemyHost?.Added(__result);
    }
}

[HarmonyPatch]
internal static class EnemyRemovedReplication_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.RemoveEnemyWithComponents));
        yield return AccessTools.Method(typeof(SpaceSector), nameof(SpaceSector.RemoveEnemyWithComponents));
    }
    [HarmonyPrefix]
    private static void Prefix(object __instance, int id, out ObjectKey __state)
    {
        __state = default;
        if (Multiplayer.Session?.IsServer != true) return;
        var runtime = Multiplayer.Session.AuthorityRuntime;
        if (__instance is PlanetFactory factory) runtime?.GroundEnemyHost?.TryGetEnemyKey(factory.planetId, id, out __state);
        else runtime?.SpaceEnemyHost?.TryGetEnemyKey(id, out __state);
    }
    [HarmonyPostfix]
    internal static void Postfix(object __instance, int id, ObjectKey __state)
    {
        if (!__state.IsValid) return;
        var runtime = Multiplayer.Session.AuthorityRuntime;
        if (__instance is PlanetFactory factory)
        {
            if (factory.enemyPool[id].id == 0) runtime?.GroundEnemyHost?.Removed(__state);
        }
        else if (((SpaceSector)__instance).enemyPool[id].id == 0) runtime?.SpaceEnemyHost?.Removed(__state);
    }
}

[HarmonyPatch]
internal static class EnemyStateReplication_Patch
{
    private static AuthoritySession Runtime => Multiplayer.Session?.IsServer == true ? Multiplayer.Session.AuthorityRuntime : null;
    private static void Mark(int astro, int id)
    {
        if (Runtime is not { } runtime) return;
        if (SpaceSector.IsPlanetAstro(astro)) runtime.GroundEnemyHost?.MarkState(astro, id);
        else runtime.SpaceEnemyHost?.MarkState(id);
    }
    [HarmonyPostfix, HarmonyPatch(typeof(SkillSystem), nameof(SkillSystem.DamageObject))]
    private static void GlobalDamage(ref SkillTarget target)
    {
        if (target.type == ETargetType.Enemy) Mark(target.astroId, target.id);
        else if (target.type == ETargetType.Craft) Runtime?.CraftHost?.MarkState(!SpaceSector.IsPlanetAstro(target.astroId), target.astroId, target.id);
    }
    [HarmonyPostfix, HarmonyPatch(typeof(SkillSystem), nameof(SkillSystem.AddCombatStatHPIncoming))]
    private static void Incoming(ref SkillTarget obj)
    {
        if (obj.type == ETargetType.Enemy) Mark(obj.astroId, obj.id);
        else if (obj.type == ETargetType.Craft) Runtime?.CraftHost?.MarkState(!SpaceSector.IsPlanetAstro(obj.astroId), obj.astroId, obj.id);
    }
    [HarmonyPostfix, HarmonyPatch(typeof(SkillSystem), nameof(SkillSystem.DamageGroundObjectByLocalCaster))]
    [HarmonyPatch(typeof(SkillSystem), nameof(SkillSystem.DamageGroundObjectByRemoteCaster))]
    private static void GroundDamage(PlanetFactory factory, ref SkillTargetLocal target)
    {
        if (target.type == ETargetType.Enemy) Mark(factory.planetId, target.id);
        else if (target.type == ETargetType.Craft) Runtime?.CraftHost?.MarkState(false, factory.planetId, target.id);
    }
    [HarmonyPrefix, HarmonyPatch(typeof(CombatStat), nameof(CombatStat.TickSkillLogic))]
    private static void BeforeTick(ref CombatStat __instance, out (int Astro, int Id, int Hp, int Max, int Incoming, bool Craft) __state)
        => __state = __instance.objectType is (int)EObjectType.Enemy or (int)EObjectType.Craft
            ? (__instance.originAstroId, __instance.objectId, __instance.hp, __instance.hpMax, __instance.hpIncoming, __instance.objectType == (int)EObjectType.Craft) : default;
    [HarmonyPostfix, HarmonyPatch(typeof(CombatStat), nameof(CombatStat.TickSkillLogic))]
    private static void AfterTick(ref CombatStat __instance, (int Astro, int Id, int Hp, int Max, int Incoming, bool Craft) __state)
    {
        if (__state.Id > 0 && (__instance.hp != __state.Hp || __instance.hpMax != __state.Max || __instance.hpIncoming != __state.Incoming))
        {
            if (__state.Craft) Runtime?.CraftHost?.MarkState(!SpaceSector.IsPlanetAstro(__state.Astro), __state.Astro, __state.Id);
            else Mark(__state.Astro, __state.Id);
        }
    }
}
