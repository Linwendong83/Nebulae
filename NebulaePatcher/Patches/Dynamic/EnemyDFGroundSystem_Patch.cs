
#region
using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(EnemyDFGroundSystem))]
internal class EnemyDFGroundSystem_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.PostGameTick))]
    public static bool PostGameTick_Prefix() => !Multiplayer.IsActive || Multiplayer.Session.IsServer;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.ExecuteDeferredEnemyChange))]
    public static bool ExecuteDeferredEnemyChange_Prefix(EnemyDFGroundSystem __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;

        __instance._rmv_id_list?.Clear();
        __instance._add_bidx_list?.Clear();
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.InitiateUnitDeferred))]
    public static bool InitiateUnitDeferred_Prefix()
    {
        return !Multiplayer.IsActive || Multiplayer.Session.IsServer;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.ExecuteDeferredUnitFormation))]
    public static bool ExecuteDeferredUnitFormation_Prefix(EnemyDFGroundSystem __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        __instance._initiate_unit_list?.Clear();
        __instance._activate_unit_list?.Clear();
        __instance._deactivate_unit_list?.Clear();
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.NotifyEnemyKilled))]
    public static bool NotifyEnemyKilled_Prefix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        // Wait for server to authorize
        return Multiplayer.Session.Combat.IsIncomingRequest ||
               Multiplayer.Session.AuthorityRuntime.ApplyContext.IsActiveOnCurrentThread;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.NotifyBaseRemoving))]
    public static bool NotifyBaseRemoving_Prefix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        // Wait for DFRelayLeaveBasePacket from server
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.PostKeyTick))]
    public static bool PostKeyTick_Prefix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        // Don't remove unit in formation because it may still waiting for server
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.RemoveBasePit))]
    public static bool RemoveBasePit_Prefix(EnemyDFGroundSystem __instance, int pitRuinId)
    {
        if (!Multiplayer.IsActive) return true;

        if (pitRuinId <= 0 || __instance.factory.ruinPool == null || pitRuinId >= __instance.factory.ruinCursor ||
            __instance.factory.ruinPool[pitRuinId].id != pitRuinId) return false;

        var buffer = __instance.bases.buffer;
        var cursor = __instance.bases.cursor;
        for (var baseId = 1; baseId < cursor; baseId++)
        {
            var dfgbaseComponent = buffer[baseId];
            if (dfgbaseComponent?.id == baseId && dfgbaseComponent.ruinId == pitRuinId)
            {
                if (Multiplayer.Session.IsServer)
                {
                    return true;
                }
                else
                {
                    // Client waits for the host's world state.
                    return Multiplayer.Session.Factories.IsIncomingRequest.Value ||
                        Multiplayer.Session.AuthorityRuntime.ApplyContext.IsActiveOnCurrentThread;
                }
            }
        }
        // Is it possible to go here in vanilla?
        __instance.factory.RemoveRuinWithComponet(pitRuinId);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.RemoveDFGBaseComponent))]
    public static bool RemoveDFGBaseComponent_Prefix()
    {
        if (!Multiplayer.IsActive) return true;

        if (Multiplayer.Session.IsServer)
        {
            return true;
        }

        // Client should wait for server to approve the removal of base from the base buffer
        return Multiplayer.Session.Combat.IsIncomingRequest ||
               Multiplayer.Session.AuthorityRuntime.ApplyContext.IsActiveOnCurrentThread;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.GameTickLogic_Base))]
    public static bool GameTickLogic_Base_Prefix() => !Multiplayer.IsActive || Multiplayer.Session.IsServer;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.GameTickLogic_Anim))]
    public static bool GameTickLogic_Anim_Prefix() => !Multiplayer.IsActive || Multiplayer.Session.IsServer;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.ClearReferencesOnEnemyRemove))]
    public static bool ClearReferencesOnEnemyRemove_Prefix(EnemyDFGroundSystem __instance, int removingEnemyId)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        // The remaining native method follows builder/formation AI pointers, which a
        // presentation base intentionally does not own. Target cleanup still must run.
        NebulaeWorld.Combat.EnemyTargetIdentity.RemoveGroundReferences(__instance.factory, removingEnemyId);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.KeyTickLogic))]
    public static bool KeyTickLogic_Prefix()
    {
        return !Multiplayer.IsActive || Multiplayer.Session.IsServer;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFGroundSystem.GameTickLogic_Unit))]
    public static bool GameTickLogic_Unit_Prefix(EnemyDFGroundSystem __instance)
    {
        return !Multiplayer.IsActive || Multiplayer.Session.IsServer ||
            NebulaeWorld.Combat.NativeEnemySimulation.CanRun(__instance.factory);
    }

}
