#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic.Combat;

[HarmonyPatch(typeof(DFRelayComponent))]
internal class DFRelayComponent_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(DFRelayComponent.SearchTargetPlaceProcess))]
    public static bool SearchTargetPlaceProcess_Prefix()
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsServer) return true;

        // Let server perform the search target and LeaveDock()
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DFRelayComponent.RealizePlanetBase))]
    public static bool RealizePlanetBase_Prefix()
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient) return Multiplayer.Session.Enemies.IsIncomingRelayRequest;

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DFRelayComponent.ArriveBase))]
    public static bool ArriveBase_Prefix()
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient) return Multiplayer.Session.Enemies.IsIncomingRelayRequest;

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DFRelayComponent.ArriveDock))]
    public static bool ArriveDock_Prefix()
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient) return Multiplayer.Session.Enemies.IsIncomingRelayRequest;

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DFRelayComponent.LeaveBase))]
    public static bool LeaveBase_Prefix()
    {
        if (!Multiplayer.IsActive) return true;

        if (Multiplayer.Session.IsClient) return Multiplayer.Session.Enemies.IsIncomingRelayRequest;

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DFRelayComponent.LeaveDock))]
    public static bool LeaveDock_Prefix()
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient) return Multiplayer.Session.Enemies.IsIncomingRelayRequest;

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DFRelayComponent.LogicTickBaseMaintain))]
    public static bool LogicTickBaseMaintain_Prefix(DFRelayComponent __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;

        if (__instance.baseState == DFRelayComponent.REALIZED_BASE && __instance.hive.galaxy.astrosFactory[__instance.targetAstroId] == null)
        {
            //the target factory is not loaded on client
            return false;
        }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DFRelayComponent.RelaySailLogic))]
    public static bool RelaySailLogic_Prefix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;

        // A relay's landing target (targetLPos/targetYaw) is not part of the replicated state, so the
        // native sail logic would steer a client's relay towards the planet's local origin - straight
        // through the surface - instead of the point the host is landing on. The client's relay is a
        // mirror: pose and animation arrive with every space-enemy state record, and the direction
        // and stage changes are server-owned anyway.
        return false;
    }
}
