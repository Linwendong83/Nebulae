using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(DefenseSystem))]
internal static class DefenseTargets_Patch
{
    [HarmonyPostfix, HarmonyPatch(nameof(DefenseSystem.Import))]
    private static void AfterImport(DefenseSystem __instance)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient) Prune(__instance, true);
    }

    [HarmonyPrefix, HarmonyPatch(nameof(DefenseSystem.GameTick))]
    [HarmonyPatch(nameof(DefenseSystem.PostGameTick))]
    private static void BeforeTick(DefenseSystem __instance)
    {
        if (Multiplayer.IsActive) Prune(__instance, false);
    }

    internal static void Prune(DefenseSystem defense, bool imported)
    {
        var targets = defense.globalTargets;
        if (targets == null) return;
        // DefenseSystem runs before per-turret readiness checks. Imported host enemy indexes
        // cannot be dereferenced while the sector baseline still contains an empty enemy pool.
        defense.localGlobalTargetCursor = System.Math.Min(defense.localGlobalTargetCursor, targets.Length);
        var count = imported ? targets.Length : defense.localGlobalTargetCursor;
        for (var index = 0; index < count; index++)
        {
            ref var target = ref targets[index];
            if (target.type != ETargetType.Enemy) continue;
            var skillTarget = new SkillTarget { type = target.type, id = target.id, astroId = target.astroId };
            if (imported || !EnemyTargetIdentity.TryResolve(skillTarget, out _))
                target.id = 0;
        }
        defense.ArrangeGlobalTargets();
    }
}
