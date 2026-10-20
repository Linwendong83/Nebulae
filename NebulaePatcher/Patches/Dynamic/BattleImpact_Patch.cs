using System;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch]
internal static class BattleImpact_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GeneralMissile), nameof(GeneralMissile.TickSkillLogic))]
    public static void Missile_Prefix(ref GeneralMissile __instance, SkillSystem skillSystem, out BattleImpactCapture.WorldCapture __state)
    {
        __state = Begin(WorldOrEnemy(__instance.caster.type), skillSystem, __instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GeneralProjectile), nameof(GeneralProjectile.TickSkillLogic))]
    public static void Projectile_Prefix(ref GeneralProjectile __instance, SkillSystem skillSystem, out BattleImpactCapture.WorldCapture __state)
    {
        __state = Begin(WorldOrEnemy(__instance.caster.type), skillSystem, __instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SpaceLaserSweep), nameof(SpaceLaserSweep.TickSkillLogic))]
    public static void Sweep_Prefix(ref SpaceLaserSweep __instance, SkillSystem skillSystem, out BattleImpactCapture.WorldCapture __state)
    {
        __state = Begin(__instance.caster.type == ETargetType.Enemy, skillSystem, __instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GeneralExpImpProjectile), nameof(GeneralExpImpProjectile.TickSkillLogic))]
    public static void Bomber_Prefix(ref GeneralExpImpProjectile __instance, SkillSystem skillSystem, out BattleImpactCapture.WorldCapture __state)
    {
        __state = Begin(__instance.caster.type == ETargetType.Enemy, skillSystem, __instance);
    }

    // Ground enemy bullets and lasers (ranger/guardian plasmas, raider lasers, tower lasers) are the
    // most common source of the mecha shield hit flash (hitEffects[8]). The space pool covers the
    // lancer/warship side. Player casters never hit a shield, so they stay out of the capture.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LocalGeneralProjectile), nameof(LocalGeneralProjectile.TickSkillLogic))]
    public static void LocalProjectile_Prefix(ref LocalGeneralProjectile __instance, SkillSystem skillSystem, out BattleImpactCapture.WorldCapture __state)
    {
        __state = Begin(WorldOrEnemy(__instance.caster.type), skillSystem, __instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LocalLaserOneShot), nameof(LocalLaserOneShot.TickSkillLogic))]
    public static void LocalLaser_Prefix(ref LocalLaserOneShot __instance, SkillSystem skillSystem, out BattleImpactCapture.WorldCapture __state)
    {
        __state = Begin(WorldOrEnemy(__instance.caster.type), skillSystem, __instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SpaceLaserOneShot), nameof(SpaceLaserOneShot.TickSkillLogic))]
    public static void SpaceLaser_Prefix(ref SpaceLaserOneShot __instance, SkillSystem skillSystem, out BattleImpactCapture.WorldCapture __state)
    {
        __state = Begin(WorldOrEnemy(__instance.caster.type), skillSystem, __instance);
    }

    private static bool WorldOrEnemy(ETargetType caster) => caster == ETargetType.None || caster == ETargetType.Enemy || caster == ETargetType.Craft;

    private static BattleImpactCapture.WorldCapture Begin(bool capture, SkillSystem skills, IPoolElement value) => capture && Multiplayer.IsActive &&
        Multiplayer.Session.IsGameLoaded ? Multiplayer.Session.Impacts.BeginWorld(skills, value) : null;

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(GeneralMissile), nameof(GeneralMissile.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralProjectile), nameof(GeneralProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(SpaceLaserSweep), nameof(SpaceLaserSweep.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralExpImpProjectile), nameof(GeneralExpImpProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(LocalGeneralProjectile), nameof(LocalGeneralProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(LocalLaserOneShot), nameof(LocalLaserOneShot.TickSkillLogic))]
    [HarmonyPatch(typeof(SpaceLaserOneShot), nameof(SpaceLaserOneShot.TickSkillLogic))]
    public static Exception Finalizer(SkillSystem skillSystem, BattleImpactCapture.WorldCapture __state, Exception __exception)
    {
        if (__state != null && Multiplayer.IsActive) Multiplayer.Session.Impacts.EndWorld(skillSystem, __state);
        return __exception;
    }
}

[HarmonyPatch(typeof(DataPoolRenderer<ParticleData>), nameof(DataPoolRenderer<ParticleData>.Render))]
internal static class PredictedWorldImpactRendering_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(DataPoolRenderer<ParticleData> __instance) => !Multiplayer.IsActive ||
        !Multiplayer.Session.IsClient || !Multiplayer.Session.IsGameLoaded || !Multiplayer.Session.Impacts.RenderNative(__instance);
}
