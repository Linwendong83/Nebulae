#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch]
internal class SkillSystem_Common_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Bomb_Explosive), nameof(Bomb_Explosive.TickSkillLogic))]
    [HarmonyPatch(typeof(Bomb_Liquid), nameof(Bomb_Liquid.TickSkillLogic))]
    [HarmonyPatch(typeof(Bomb_EMCapsule), nameof(Bomb_EMCapsule.TickSkillLogic))]
    public static void Bomb_TickSkillLogic(ref int ___nearPlanetAstroId, ref int ___life)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient) return;
        if (___nearPlanetAstroId > 0 && GameMain.spaceSector.skillSystem.astroFactories[___nearPlanetAstroId] == null)
        {
            // The nearest planetFactory hasn't loaded yet, skip and remove
            ___life = 0;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GeneralShieldBurst), nameof(GeneralShieldBurst.TickSkillLogic))]
    public static bool GeneralShieldBurst_Prefix(ref GeneralShieldBurst __instance, SkillSystem skillSystem)
    {
        if (!Multiplayer.IsActive || __instance.caster.type != ETargetType.Player) return true;
        if (!Multiplayer.Session.Combat.IndexByPlayerId.TryGetValue(__instance.caster.id, out var index)) return true;

        // Overwrite the original function
        SwitchPlayerState(__instance.caster.id);
        ref var playerData = ref Multiplayer.Session.Combat.Players[index];
        __instance.upos = playerData.mecha.skillShieldBurstUCenter;
        __instance.rpos = playerData.mecha.skillShieldBurstLCenter;
        if ((__instance.lifeMax - __instance.life) % __instance.damageInterval == 0)
        {
            __instance.DoRangeDamage(skillSystem);
        }
        if (__instance.life > 0)
        {
            __instance.life--;
        }
        return false;
    }

    static void SwitchPlayerState(int playerId)
    {
        if (!Multiplayer.Session.Combat.IndexByPlayerId.TryGetValue(playerId, out var index)) return;

        ref var playerData = ref Multiplayer.Session.Combat.Players[index];
        var skillSystem = GameMain.data.spaceSector.skillSystem;
        if (playerData.mecha?.player?.transform == null) return;
        skillSystem.mecha = playerData.mecha;
        skillSystem.playerAlive = playerData.isAlive;
        SetCollisionPose(skillSystem, ref playerData, playerData.mecha.skillTargetLCenter, playerData.mecha.skillTargetUCenter);
        skillSystem.playerSkillCastLeftL = playerData.mecha.skillCastLeftL;
        skillSystem.playerSkillCastLeftU = playerData.mecha.skillCastLeftU;
        skillSystem.playerSkillCastRightL = playerData.mecha.skillCastRightL;
        skillSystem.playerSkillCastRightU = playerData.mecha.skillCastRightU;
        SetCollisionState(skillSystem, ref playerData);
    }

    static void SwitchTargetPlayerWithCollider(int playerId)
    {
        if (!Multiplayer.Session.Combat.IndexByPlayerId.TryGetValue(playerId, out var index)) return;

        ref var playerData = ref Multiplayer.Session.Combat.Players[index];
        var skillSystem = GameMain.data.spaceSector.skillSystem;
        if (playerData.mecha?.player?.transform == null) return;
        skillSystem.mecha = playerData.mecha;
        skillSystem.playerAlive = playerData.isAlive;
        SetCollisionPose(skillSystem, ref playerData, playerData.mecha.skillTargetLCenter, playerData.mecha.skillTargetUCenter);
        SetCollisionState(skillSystem, ref playerData);
    }

    private static void SetCollisionState(SkillSystem skills, ref NebulaeWorld.Combat.CombatManager.PlayerPosition player)
    {
        skills.playerAstroId = skills.localPlanetAstroId = System.Math.Max(0, player.planetId);
        skills.playerIsSailing = player.mecha.player.sailing;
        skills.playerIsWarping = player.mecha.player.warping;
        skills.playerSkillColliderL = player.mecha.skillColliderL;
        skills.playerSkillColliderU = player.mecha.skillColliderU;
        skills.playerSkillColliderL.idType = (skills.playerSkillColliderL.idType & ~0xFFFFFF) | player.id;
        skills.playerSkillColliderU.idType = (skills.playerSkillColliderU.idType & ~0xFFFFFF) | player.id;
        skills.playerEnergyShieldRadius = NebulaeWorld.Combat.CombatRecoveryView.Display(player.mecha).energyShieldEnergy >= player.mecha.energyShieldEnergyRate
            ? player.mecha.energyShieldRadius * player.mecha.energyShieldRadiusMultiplier : 0;
        skills.playerVelocityU = player.mecha.player.uVelocity;
        skills.playerVelocityL = player.mecha.player.controller.velocity;
        skills.playerAltL = player.mecha.player.planetData == null ? 0 : player.position.magnitude - player.mecha.player.planetData.realRadius;
    }

    internal static void SetCollisionPose(SkillSystem skills, ref NebulaeWorld.Combat.CombatManager.PlayerPosition player,
        UnityEngine.Vector3 currentLocal, VectorLF3 currentUniversal)
    {
        // The table is captured after LogicFrame. Before the next native frame the
        // host accepts a newer actor pose and updates its body collider. Use that
        // same current pose for the shield; the table's current entry is LAST tick.
        // Reading table.skillTargetULast here uses two old samples and can leave
        // the shield 100-200 m behind a body after a high-speed movement packet.
        skills.playerSkillTargetL = currentLocal;
        skills.playerSkillTargetULast = player.skillTargetU;
        skills.playerSkillTargetU = currentUniversal;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GeneralExpImpProjectile), nameof(GeneralExpImpProjectile.TickSkillLogic))]
    public static void GeneralExpImpProjectile_Prefix(ref GeneralExpImpProjectile __instance)
    {
        if (!Multiplayer.IsActive) return;
        if (__instance.caster.type == ETargetType.Player) SwitchPlayerState(__instance.caster.id);
        if (__instance.target.type == ETargetType.Player) SwitchTargetPlayerWithCollider(__instance.target.id);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GeneralMissile), nameof(GeneralMissile.TickSkillLogic))]
    public static void GeneralProjectile_Prefix(ref GeneralMissile __instance)
    {
        if (!Multiplayer.IsActive) return;
        if (__instance.caster.type == ETargetType.Player) SwitchPlayerState(__instance.caster.id);
        if (__instance.target.type == ETargetType.Player) SwitchTargetPlayerWithCollider(__instance.target.id);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GeneralProjectile), nameof(GeneralProjectile.TickSkillLogic))]
    public static void GeneralProjectile_Prefix(ref GeneralProjectile __instance)
    {
        if (!Multiplayer.IsActive) return;
        if (__instance.caster.type == ETargetType.Player) SwitchPlayerState(__instance.caster.id);
        if (__instance.target.type == ETargetType.Player) SwitchTargetPlayerWithCollider(__instance.target.id);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LocalGeneralProjectile), nameof(LocalGeneralProjectile.TickSkillLogic))]
    public static void LocalGeneralProjectile_Prefix(ref LocalGeneralProjectile __instance)
    {
        if (!Multiplayer.IsActive) return;
        if (__instance.caster.type == ETargetType.Player) SwitchPlayerState(__instance.caster.id);
        if (__instance.target.type == ETargetType.Player) SwitchTargetPlayerWithCollider(__instance.target.id);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LocalLaserContinuous), nameof(LocalLaserContinuous.TickSkillLogic))]
    public static void LocalLaserContinuous_Prefix(ref LocalLaserContinuous __instance)
    {
        if (!Multiplayer.IsActive) return;
        if (__instance.caster.type == ETargetType.Player) SwitchPlayerState(__instance.caster.id);
        if (__instance.target.type == ETargetType.Player) SwitchPlayerState(__instance.target.id);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LocalLaserOneShot), nameof(LocalLaserOneShot.TickSkillLogic))]
    public static void LocalLaserOneShot_Prefix(ref LocalLaserOneShot __instance)
    {
        if (!Multiplayer.IsActive) return;
        if (__instance.caster.type == ETargetType.Player) SwitchPlayerState(__instance.caster.id);
        if (__instance.target.type == ETargetType.Player) SwitchPlayerState(__instance.target.id);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LocalCannonade), nameof(LocalCannonade.TickSkillLogic))]
    public static void LocalCannonade_Prefix(ref LocalCannonade __instance)
    {
        if (!Multiplayer.IsActive) return;
        if (__instance.caster.type == ETargetType.Player) SwitchPlayerState(__instance.caster.id);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SpaceLaserOneShot), nameof(SpaceLaserOneShot.TickSkillLogic))]
    public static void SpaceLaserOneShot_Prefix(ref SpaceLaserOneShot __instance)
    {
        if (!Multiplayer.IsActive) return;
        if (__instance.caster.type == ETargetType.Player) SwitchPlayerState(__instance.caster.id);
        if (__instance.target.type == ETargetType.Player) SwitchPlayerState(__instance.target.id);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SpaceLaserSweep), nameof(SpaceLaserSweep.TickSkillLogic))]
    public static void SpaceLaserSweep_Prefix(ref SpaceLaserSweep __instance)
    {
        if (!Multiplayer.IsActive) return;
        if (__instance.caster.type == ETargetType.Player) SwitchPlayerState(__instance.caster.id);
        if ((__instance.mask & ETargetTypeMask.Player) != 0) SwitchTargetPlayerWithCollider(Multiplayer.Session.LocalPlayer.Id);
    }
}
