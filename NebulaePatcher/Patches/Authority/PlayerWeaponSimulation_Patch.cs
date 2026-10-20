using System;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class PlayerWeaponSimulation_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Bomb_Explosive), nameof(Bomb_Explosive.BombFactoryObjects))]
    [HarmonyPatch(typeof(Bomb_EMCapsule), nameof(Bomb_EMCapsule.BombFactoryObjects))]
    public static bool BombWorldWrites() => !Multiplayer.IsActive || !Multiplayer.Session.IsClient;

    [HarmonyPrefix, HarmonyPatch(typeof(DFGBaseComponent), nameof(DFGBaseComponent.AddIncomingSkill))]
    public static bool IncomingSkill() => !Multiplayer.IsActive || !Multiplayer.Session.IsClient || !PlayerWeaponResources.Active;

    [HarmonyPrefix, HarmonyPatch(typeof(SkillAudioLogic), nameof(SkillAudioLogic.PlayPlanetParticleSFX))]
    public static bool ImpactAudio() => !PlayerWeaponManager.SuppressImpactAudio && !BattleImpactCapture.SuppressNativeAudio;

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(LocalGeneralProjectile), nameof(LocalGeneralProjectile.TickSkillLogic))]
    public static bool LocalGeneralProjectile_Prefix(ref LocalGeneralProjectile __instance, SkillSystem skillSystem, out PlayerWeaponManager.TickScope __state) =>
        Enter(__instance, skillSystem, out __state);

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(LocalLaserOneShot), nameof(LocalLaserOneShot.TickSkillLogic))]
    public static bool LocalLaserOneShot_Prefix(ref LocalLaserOneShot __instance, SkillSystem skillSystem, out PlayerWeaponManager.TickScope __state) =>
        Enter(__instance, skillSystem, out __state);

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(SpaceLaserOneShot), nameof(SpaceLaserOneShot.TickSkillLogic))]
    public static bool SpaceLaserOneShot_Prefix(ref SpaceLaserOneShot __instance, SkillSystem skillSystem, out PlayerWeaponManager.TickScope __state) =>
        Enter(__instance, skillSystem, out __state);

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(GeneralProjectile), nameof(GeneralProjectile.TickSkillLogic))]
    public static bool GeneralProjectile_Prefix(ref GeneralProjectile __instance, SkillSystem skillSystem, out PlayerWeaponManager.TickScope __state) =>
        Enter(__instance, skillSystem, out __state);

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(GeneralMissile), nameof(GeneralMissile.TickSkillLogic))]
    public static bool GeneralMissile_Prefix(ref GeneralMissile __instance, SkillSystem skillSystem, out PlayerWeaponManager.TickScope __state) =>
        Enter(__instance, skillSystem, out __state);

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(LocalCannonade), nameof(LocalCannonade.TickSkillLogic))]
    public static bool LocalCannonade_Prefix(ref LocalCannonade __instance, SkillSystem skillSystem, out PlayerWeaponManager.TickScope __state) =>
        Enter(__instance, skillSystem, out __state);

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(GeneralCannonade), nameof(GeneralCannonade.TickSkillLogic))]
    public static bool GeneralCannonade_Prefix(ref GeneralCannonade __instance, SkillSystem skillSystem, out PlayerWeaponManager.TickScope __state) =>
        Enter(__instance, skillSystem, out __state);

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Bomb_Explosive), nameof(Bomb_Explosive.TickSkillLogic))]
    public static bool Bomb_Explosive_Prefix(ref Bomb_Explosive __instance, SkillSystem skillSystem, out PlayerWeaponManager.TickScope __state) =>
        Enter(__instance, skillSystem, out __state);

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Bomb_Liquid), nameof(Bomb_Liquid.TickSkillLogic))]
    public static bool Bomb_Liquid_Prefix(ref Bomb_Liquid __instance, SkillSystem skillSystem, out PlayerWeaponManager.TickScope __state) =>
        Enter(__instance, skillSystem, out __state);

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Bomb_EMCapsule), nameof(Bomb_EMCapsule.TickSkillLogic))]
    public static bool Bomb_EMCapsule_Prefix(ref Bomb_EMCapsule __instance, SkillSystem skillSystem, out PlayerWeaponManager.TickScope __state) =>
        Enter(__instance, skillSystem, out __state);

    private static bool Enter(IPoolElement skill, SkillSystem skillSystem, out PlayerWeaponManager.TickScope state)
    {
        state = null;
        if (!Multiplayer.IsActive) return true;
        state = Multiplayer.Session.PlayerWeapons.BeforeSkill(skill, skillSystem, out var run);
        return run;
    }

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(GeneralShieldBurst), nameof(GeneralShieldBurst.TickSkillLogic))]
    public static bool ShieldBurst_Prefix(ref GeneralShieldBurst __instance, SkillSystem skillSystem, out PlayerWeaponManager.TickScope __state) =>
        Enter(__instance, skillSystem, out __state);

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(GeneralShieldBurst), nameof(GeneralShieldBurst.TickSkillLogic))]
    [HarmonyPatch(typeof(LocalGeneralProjectile), nameof(LocalGeneralProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(LocalLaserOneShot), nameof(LocalLaserOneShot.TickSkillLogic))]
    [HarmonyPatch(typeof(SpaceLaserOneShot), nameof(SpaceLaserOneShot.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralProjectile), nameof(GeneralProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralMissile), nameof(GeneralMissile.TickSkillLogic))]
    [HarmonyPatch(typeof(LocalCannonade), nameof(LocalCannonade.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralCannonade), nameof(GeneralCannonade.TickSkillLogic))]
    [HarmonyPatch(typeof(Bomb_Explosive), nameof(Bomb_Explosive.TickSkillLogic))]
    [HarmonyPatch(typeof(Bomb_Liquid), nameof(Bomb_Liquid.TickSkillLogic))]
    [HarmonyPatch(typeof(Bomb_EMCapsule), nameof(Bomb_EMCapsule.TickSkillLogic))]
    public static Exception Finalizer(PlayerWeaponManager.TickScope __state, Exception __exception)
    {
        __state?.Dispose(); return __exception;
    }
}
