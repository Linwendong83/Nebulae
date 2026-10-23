#region

using HarmonyLib;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeWorld;
using NebulaeWorld.Universe;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(DysonSwarm))]
internal class DysonSwarm_Patch
{
    private static Vector4 storedHsva;

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
        // Notify others about manual sails cleaning.
        // In DysonSwarm.GameTick() it will automatically clean every 21600 ticks
        if (!Multiplayer.Session.DysonSpheres.IncomingDysonSwarmPacket && GameMain.gameTick % 21600L != 0L)
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
        if (storedHsva == hsva)
        {
            return;
        }
        Multiplayer.Session.Network.SendPacket(new DysonSwarmEditOrbitPacket(__instance.starData.index, orbitId, hsva));
        storedHsva = hsva;
    }
}
