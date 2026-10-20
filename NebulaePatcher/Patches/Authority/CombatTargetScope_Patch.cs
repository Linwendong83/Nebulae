using System;
using HarmonyLib;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class CombatTargetScope_Patch
{
    // Every projectile tick saves the shared "current player" view, redirects it to the
    // authoritative target and restores it on the way out — including exceptional exits.
    // Per-thread scope stacks preserve nesting without boxing scope structs.
    private static void Enter(SkillSystem skillSystem) =>
        ScopeStack<PlayerSkillScope>.Push(new PlayerSkillScope(skillSystem));

    private static void SetTarget(SkillTargetLocal target) =>
        CombatTargetContext.TargetMecha = target.type == ETargetType.Player && CombatTargetContext.IsPlayerTarget(target.id)
            ? CombatTargetContext.ResolveDamageMecha(GameMain.spaceSector.skillSystem, target.id) : null;

    private static void SetTarget(SkillTarget target) =>
        CombatTargetContext.TargetMecha = target.type == ETargetType.Player && CombatTargetContext.IsPlayerTarget(target.id)
            ? CombatTargetContext.ResolveDamageMecha(GameMain.spaceSector.skillSystem, target.id) : null;

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(LocalGeneralProjectile), nameof(LocalGeneralProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(LocalLaserOneShot), nameof(LocalLaserOneShot.TickSkillLogic))]
    [HarmonyPatch(typeof(LocalLaserContinuous), nameof(LocalLaserContinuous.TickSkillLogic))]
    public static void Local_Prefix(SkillSystem skillSystem, SkillTargetLocal ___target)
    {
        Enter(skillSystem);
        SetTarget(___target);
    }

    private static bool HostSkills(SkillSystem skills) => NebulaeWorld.Multiplayer.IsActive && NebulaeWorld.Multiplayer.Session.IsServer &&
        ReferenceEquals(skills, GameMain.spaceSector.skillSystem);

    private static bool LocalSkills(SkillSystem skills, SkillTargetLocal target) => NebulaeWorld.Multiplayer.IsActive &&
        (NebulaeWorld.Multiplayer.Session.IsServer || target.type == ETargetType.Player) &&
        ReferenceEquals(skills, GameMain.spaceSector.skillSystem);

    [HarmonyPrefix, HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(LocalLaserOneShot), nameof(LocalLaserOneShot.TickSkillLogic))]
    public static bool LaserLifetime(SkillSystem skillSystem, ref LocalLaserOneShot __instance)
    {
        if (!LocalSkills(skillSystem, __instance.target) || LocalSkillLifetime.IsValid(skillSystem, __instance.astroId, __instance.caster, __instance.target)) return true;
        __instance.life = 0; return false;
    }

    [HarmonyPrefix, HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(LocalGeneralProjectile), nameof(LocalGeneralProjectile.TickSkillLogic))]
    public static bool ProjectileLifetime(SkillSystem skillSystem, ref LocalGeneralProjectile __instance)
    {
        if (!LocalSkills(skillSystem, __instance.target) || LocalSkillLifetime.IsValid(skillSystem, __instance.astroId, __instance.caster, __instance.target)) return true;
        __instance.life = 0; return false;
    }

    [HarmonyPrefix, HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(LocalLaserContinuous), nameof(LocalLaserContinuous.TickSkillLogic))]
    public static bool ContinuousLifetime(SkillSystem skillSystem, ref LocalLaserContinuous __instance)
    {
        if (!LocalSkills(skillSystem, __instance.target) || LocalSkillLifetime.IsValid(skillSystem, __instance.astroId, __instance.caster, __instance.target)) return true;
        __instance.damage = 0; __instance.fade = 0; return false;
    }

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(GeneralProjectile), nameof(GeneralProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralExpImpProjectile), nameof(GeneralExpImpProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralMissile), nameof(GeneralMissile.TickSkillLogic))]
    [HarmonyPatch(typeof(SpaceLaserOneShot), nameof(SpaceLaserOneShot.TickSkillLogic))]
    public static bool Space_Prefix(SkillSystem skillSystem, SkillTarget ___target, ref int ___life)
    {
        Enter(skillSystem);
        if (HostSkills(skillSystem) && ___target.type == ETargetType.Player && !CombatTargetContext.IsPlayerTarget(___target.id))
        {
            CombatTargetContext.TargetMecha = null;
            ___life = 0;
            return false;
        }
        SetTarget(___target);
        return true;
    }

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(LocalCannonade), nameof(LocalCannonade.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralShieldBurst), nameof(GeneralShieldBurst.TickSkillLogic))]
    [HarmonyPatch(typeof(Bomb_Explosive), nameof(Bomb_Explosive.TickSkillLogic))]
    [HarmonyPatch(typeof(Bomb_Liquid), nameof(Bomb_Liquid.TickSkillLogic))]
    [HarmonyPatch(typeof(Bomb_EMCapsule), nameof(Bomb_EMCapsule.TickSkillLogic))]
    public static void Caster_Prefix(SkillSystem skillSystem)
    {
        Enter(skillSystem);
        CombatTargetContext.TargetMecha = null;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(LocalGeneralProjectile), nameof(LocalGeneralProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(LocalLaserOneShot), nameof(LocalLaserOneShot.TickSkillLogic))]
    [HarmonyPatch(typeof(LocalLaserContinuous), nameof(LocalLaserContinuous.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralProjectile), nameof(GeneralProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralExpImpProjectile), nameof(GeneralExpImpProjectile.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralMissile), nameof(GeneralMissile.TickSkillLogic))]
    [HarmonyPatch(typeof(SpaceLaserOneShot), nameof(SpaceLaserOneShot.TickSkillLogic))]
    [HarmonyPatch(typeof(LocalCannonade), nameof(LocalCannonade.TickSkillLogic))]
    [HarmonyPatch(typeof(GeneralShieldBurst), nameof(GeneralShieldBurst.TickSkillLogic))]
    [HarmonyPatch(typeof(Bomb_Explosive), nameof(Bomb_Explosive.TickSkillLogic))]
    [HarmonyPatch(typeof(Bomb_Liquid), nameof(Bomb_Liquid.TickSkillLogic))]
    [HarmonyPatch(typeof(Bomb_EMCapsule), nameof(Bomb_EMCapsule.TickSkillLogic))]
    public static Exception Finalizer(Exception __exception)
    {
        ScopeStack<PlayerSkillScope>.Pop();
        return __exception;
    }
}
