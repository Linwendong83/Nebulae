using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class TurretAuthority_Patch
{
    private static bool CanSimulate(PlanetFactory factory, int entity) => !WorldSimulation.IsClient ||
        Multiplayer.Session?.AuthorityRuntime?.TurretReplica?.CanSimulate(factory, entity, GameMain.gameTick) == true;

    [HarmonyPrefix, HarmonyPatch(typeof(TurretComponent), nameof(TurretComponent.InternalUpdate))]
    public static bool Internal_Prefix(ref TurretComponent __instance, PlanetFactory factory)
    {
        if (!CanSimulate(factory, __instance.entityId)) return false;
        if (Multiplayer.IsActive) EnemyTargetIdentity.Validate(factory, ref __instance);
        return true;
    }
    [HarmonyPrefix, HarmonyPatch(typeof(TurretComponent), nameof(TurretComponent.Aim))]
    public static bool Aim_Prefix(ref TurretComponent __instance, PlanetFactory factory) => CanSimulate(factory, __instance.entityId) &&
        (!Multiplayer.IsActive || EnemyTargetIdentity.Validate(factory, ref __instance));
    [HarmonyPrefix, HarmonyPatch(typeof(TurretComponent), nameof(TurretComponent.Shoot))]
    public static bool Shoot_Prefix(ref TurretComponent __instance, PlanetFactory factory) => CanSimulate(factory, __instance.entityId) &&
        (!Multiplayer.IsActive || EnemyTargetIdentity.Validate(factory, ref __instance));
    [HarmonyPrefix, HarmonyPatch(typeof(TurretComponent), nameof(TurretComponent.PostGameTick))]
    public static bool Post_Prefix(ref TurretComponent __instance, PlanetFactory factory) => CanSimulate(factory, __instance.entityId);
    [HarmonyPrefix, HarmonyPatch(typeof(TurretComponent), nameof(TurretComponent.Search))]
    public static bool Search_Prefix(ref TurretComponent __instance, PlanetFactory factory)
    {
        if (!CanSimulate(factory, __instance.entityId)) return false;
        if (Multiplayer.IsActive) __instance.target.type = ETargetType.Enemy;
        return true;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(TurretComponent), nameof(TurretComponent.Search))]
    public static void Search_Postfix(ref TurretComponent __instance, PlanetFactory factory)
    {
        if (Multiplayer.IsActive && CanSimulate(factory, __instance.entityId))
        {
            EnemyTargetIdentity.Observe(factory, __instance);
            EnemyTargetIdentity.Validate(factory, ref __instance);
        }
    }

    [HarmonyPrefix, HarmonyPatch(typeof(TurretComponent), nameof(TurretComponent.DeterminActiveEnemyUnits))]
    public static bool Activation_Prefix(ref bool __result)
    {
        if (!WorldSimulation.IsClient) return true;
        __result = false;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(DefenseSystem), nameof(DefenseSystem.RemoveTurretComponent))]
    public static void Remove_Prefix(DefenseSystem __instance, int id)
    {
        if (!Multiplayer.IsActive || id <= 0 || id >= __instance.turrets.cursor) return;
        if (Multiplayer.Session.IsClient)
        {
            ref var turret = ref __instance.turrets.buffer[id];
            if (Multiplayer.Session.AuthorityRuntime?.TurretReplica?.HasInstalled(__instance.factory, turret.entityId) != true)
                turret.projectileId = 0;
            return;
        }
        Multiplayer.Session.Turrets.HostView?.Retire(__instance.factory.planetId, __instance.turrets.buffer[id].entityId);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(DefenseSystem), nameof(DefenseSystem.NewTurretComponent))]
    public static void Add_Postfix(DefenseSystem __instance, int __result)
    {
        if (Multiplayer.Session?.IsServer != true || __result <= 0) return;
        Multiplayer.Session.Turrets.HostView?.Added(__instance.factory, __instance.turrets.buffer[__result].entityId);
    }

    public static IReadOnlyList<string> VerifyCoverage()
    {
        var failures = new List<string>();
        foreach (var (type, method, prefix) in new[]
        {
            (typeof(TurretComponent), "InternalUpdate", "Internal_Prefix"),
            (typeof(TurretComponent), "Aim", "Aim_Prefix"),
            (typeof(TurretComponent), "Shoot", "Shoot_Prefix"),
            (typeof(TurretComponent), "PostGameTick", "Post_Prefix"),
            (typeof(TurretComponent), "Search", "Search_Prefix"),
            (typeof(TurretComponent), "DeterminActiveEnemyUnits", "Activation_Prefix")
        })
        {
            var target = AccessTools.Method(type, method);
            if (target == null || Harmony.GetPatchInfo(target)?.Prefixes.Any(p => p.PatchMethod == AccessTools.Method(typeof(TurretAuthority_Patch), prefix)) != true)
                failures.Add(type.Name + "." + method + ": turret authority prefix missing");
        }
        return failures;
    }
}
