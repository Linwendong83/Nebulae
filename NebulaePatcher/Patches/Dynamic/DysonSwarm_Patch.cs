#region

using HarmonyLib;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeModel.Utils;
using NebulaeWorld;
using NebulaeWorld.Universe;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(DysonSwarm))]
internal class DysonSwarm_Patch
{
    [HarmonyPostfix, HarmonyPatch(nameof(DysonSwarm.Import))]
    public static void Import_Postfix(DysonSwarm __instance)
    {
        // Network snapshots are validated before committing and must not silently lose sails.
        if (Multiplayer.Session?.IsClient == true && Multiplayer.Session.DysonSpheres != null &&
            Multiplayer.Session.DysonSpheres.IncomingDysonSwarmPacket) return;
        if (!Multiplayer.IsDedicated) SolarSailState.AfterImport(__instance);
    }

    [HarmonyPrefix, HarmonyPatch(nameof(DysonSwarm.SetAllSailsEternal))]
    public static bool SetAllSailsEternal_Prefix(DysonSwarm __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.DysonSpheres.IncomingDysonSwarmPacket) return true;
        if (!GameMain.data.gameDesc.isSandboxMode) return false;
        var packet = new DysonSwarmRemoveOrbitPacket(__instance.starData.index, 0, SwarmRemoveOrbitEvent.MakeSailsEternal);
        if (Multiplayer.Session.IsClient) Multiplayer.Session.Network.SendPacket(packet);
        else Multiplayer.Session.DysonSpheres.SendPacketToDysonSphere(packet, __instance.starData.index);
        return !Multiplayer.Session.IsClient;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DysonSwarm.NewOrbit))]
    public static void NewOrbit_Prefix(DysonSwarm __instance, float radius, Quaternion rotation)
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        //If local is the author and not in the process of importing blueprint
        if (Multiplayer.Session.DysonSpheres.IncomingDysonSwarmPacket || Multiplayer.Session.DysonSpheres.InBlueprint)
        {
            return;
        }
        var orbitId = DysonSphereManager.QueryOrbitId(__instance);
        Multiplayer.Session.Network.SendPacket(new DysonSwarmAddOrbitPacket(__instance.starData.index, orbitId, radius,
            rotation));
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DysonSwarm.RemoveOrbit))]
    public static void RemoveOrbit_Prefix(DysonSwarm __instance, int orbitId)
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        //If local is the author and not in the process of importing blueprint
        if (!Multiplayer.Session.DysonSpheres.IncomingDysonSwarmPacket && !Multiplayer.Session.DysonSpheres.InBlueprint)
        {
            Multiplayer.Session.Network.SendPacket(new DysonSwarmRemoveOrbitPacket(__instance.starData.index, orbitId,
                SwarmRemoveOrbitEvent.Remove));
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DysonSwarm.SetOrbitEnable))]
    public static void SetOrbitEnable_Prefix(DysonSwarm __instance, int orbitId, bool enabled)
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        //Notify others that orbit for Dyson Swarm was enabled/disabled
        if (!Multiplayer.Session.DysonSpheres.IncomingDysonSwarmPacket)
        {
            Multiplayer.Session.Network.SendPacket(new DysonSwarmRemoveOrbitPacket(__instance.starData.index, orbitId,
                enabled ? SwarmRemoveOrbitEvent.Enable : SwarmRemoveOrbitEvent.Disable));
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DysonSwarm.RemoveSailsByOrbit))]
    public static void RemoveSailsByOrbit_Prefix(DysonSwarm __instance, int orbitId)
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        // Orbit zero only compacts local slots. It also runs automatically at unrelated ticks.
        if (orbitId != 0 && !Multiplayer.Session.DysonSpheres.IncomingDysonSwarmPacket &&
            !Multiplayer.Session.DysonSpheres.InBlueprint)
        {
            Multiplayer.Session.Network.SendPacket(new DysonSwarmRemoveOrbitPacket(__instance.starData.index, orbitId,
                SwarmRemoveOrbitEvent.RemoveSails));
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DysonSwarm.EditOrbit))]
    public static void EditOrbit_Prefix(DysonSwarm __instance, int orbitId, float radius, Quaternion rotation)
    {
        if (Multiplayer.IsActive && !Multiplayer.Session.DysonSpheres.IncomingDysonSwarmPacket)
        {
            Multiplayer.Session.Network.SendPacket(new DysonSwarmEditOrbitPacket(__instance.starData.index, orbitId, radius,
                rotation));
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DysonSwarm.SetOrbitColor))]
    public static void SetOrbitColor_Prefix(DysonSwarm __instance, int orbitId, Vector4 hsva)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.DysonSpheres.IncomingDysonSwarmPacket)
        {
            return;
        }
        if (orbitId < 0 || orbitId >= __instance.orbitColorsHSVA.Length || __instance.orbitColorsHSVA[orbitId] == hsva)
        {
            return;
        }
        Multiplayer.Session.Network.SendPacket(new DysonSwarmEditOrbitPacket(__instance.starData.index, orbitId, hsva));
    }
}
