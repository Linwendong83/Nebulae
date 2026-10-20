#region

using HarmonyLib;
using NebulaeWorld;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(PlayerAction_Combat))]
internal class PlayerAction_Combat_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlayerAction_Combat.ActivateBaseEnemyManually))]
    public static bool ActivateBaseEnemyManually_Prefix(PlayerAction_Combat __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer)
        {
            return true;
        }

        var planet = __instance.localPlanet;
        if (planet?.factoryLoaded == true)
        {
            var raycast = planet.physics.raycastLogic;
            if (raycast.castEnemy.id > 0)
                Multiplayer.Session.CombatAuthority.Send(new CombatIntent(CombatIntentKind.Wake),
                    new SkillTarget { type = ETargetType.Enemy, id = raycast.castEnemy.id, astroId = planet.id });
            else if (raycast.castEnemyBaseId > 0 && Multiplayer.Session.AuthorityRuntime.WorldReplica.TryFindKey(
                         PoolKind.Base, planet.id, raycast.castEnemyBaseId, out var key))
                Multiplayer.Session.CombatAuthority.SendCommand(key, CombatIntent.Category,
                    new CombatIntent(CombatIntentKind.ActivateGroundFormation, raycast.castEnemyFormId, raycast.castEnemyPortId).Encode(), out _);
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlayerAction_Combat.ActivateNearbyEnemyBase))]
    public static bool ActivateNearbyEnemyBase_Prefix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer)
        {
            return true;
        }

        // Triggered for the host by its own AI tick (UpdateHatred); clients take host state.
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlayerAction_Combat.ActivateHiveEnemyManually))]
    public static bool ActivateHiveEnemyManually_Prefix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlayerAction_Combat.ActivateNearbyEnemyHive))]
    public static bool ActivateNearbyEnemyHive()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;

        return false;
    }
}
