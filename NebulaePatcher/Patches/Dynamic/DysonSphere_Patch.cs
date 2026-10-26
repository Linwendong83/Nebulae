#region

using HarmonyLib;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeModel.Utils;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(DysonSphere))]
internal class DysonSphere_Patch
{
    [HarmonyPostfix, HarmonyPatch(nameof(DysonSphere.Import))]
    public static void Import_Postfix(DysonSphere __instance) => SolarSailState.FinishImport(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DysonSphere.BeforeGameTick))]
    public static bool BeforeGameTick_Prefix(DysonSphere __instance, long times)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return true;
        }
        // The client replays launch effects with network delay. Keep the host's energy totals
        // and demand; a locally simulated sail count is not an authoritative power input.
        // Per-layer inspector figures may still follow the replicated construction.
        if (times % 120 == 0 && __instance.layersSorted != null)
            foreach (var layer in __instance.layersSorted)
            {
                if (layer == null) continue;
                var energy = 0L;
                for (var i = 1; i < layer.nodeCursor; i++)
                    if (layer.nodePool[i] != null && layer.nodePool[i].id == i)
                        energy += layer.nodePool[i].EnergyGenCurrentTick((int)__instance.energyGenPerNode, (int)__instance.energyGenPerFrame);
                for (var i = 1; i < layer.shellCursor; i++)
                    if (layer.shellPool[i] != null && layer.shellPool[i].id == i)
                        energy += layer.shellPool[i].cellPoint * __instance.energyGenPerShell;
                layer.energyGenCurrentTick = energy;
            }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DysonSphere.AddLayer))]
    public static void AddLayer_Postfix(DysonSphere __instance, float orbitRadius, Quaternion orbitRotation,
        float orbitAngularSpeed)
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        //If local is the author and not in the process of importing blueprint
        if (!Multiplayer.Session.DysonSpheres.IsIncomingRequest && !Multiplayer.Session.DysonSpheres.InBlueprint)
        {
            Multiplayer.Session.Network.SendPacket(new DysonSphereAddLayerPacket(__instance.starData.index,
                __instance.QueryLayerId(), orbitRadius, orbitRotation, orbitAngularSpeed));
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DysonSphere.RemoveLayer), typeof(int))]
    public static void RemoveLayer_Prefix(DysonSphere __instance, int id)
    {
        if (Multiplayer.IsActive)
        {
            RemoveLayer(id, __instance.starData.index);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(DysonSphere.RemoveLayer), typeof(DysonSphereLayer))]
    public static void RemoveLayer_Prefix2(DysonSphere __instance, DysonSphereLayer layer)
    {
        if (Multiplayer.IsActive)
        {
            RemoveLayer(layer.id, __instance.starData.index);
        }
    }

    private static void RemoveLayer(int id, int starIndex)
    {
        //If local is the author and not in the process of importing blueprint
        if (!Multiplayer.Session.DysonSpheres.IsIncomingRequest && !Multiplayer.Session.DysonSpheres.InBlueprint)
        {
            Multiplayer.Session.Network.SendPacket(new DysonSphereRemoveLayerPacket(starIndex, id));
        }
    }
}
