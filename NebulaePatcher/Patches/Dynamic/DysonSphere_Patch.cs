#region

using HarmonyLib;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(DysonSphere))]
internal class DysonSphere_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(DysonSphere.BeforeGameTick))]
    public static bool BeforeGameTick_Prefix(DysonSphere __instance, long times)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return true;
        }
        //Update swarm and layer energy generation stats every 120 frames
        if (times % 120 != 0)
        {
            return false;
        }
        // Ownership split: every number the sphere's own structure determines is computed here, on
        // the peer that owns a replica of that structure. Only the demand aggregated across all
        // planets and the hive gamma debuff arrive by packet (DysonSphereStatusPacket), because
        // those two depend on state this peer does not simulate.
        __instance.swarm.energyGenCurrentTick = __instance.swarm.sailCount * __instance.energyGenPerSail;
        var grossRadius = __instance.swarm.grossRadius;
        var energyGenOriginalCurrentTick = __instance.swarm.energyGenCurrentTick;
        for (var i = 0; i < 10; i++)
        {
            var dysonSphereLayer = __instance.layersSorted[i];
            if (dysonSphereLayer == null)
            {
                continue;
            }
            dysonSphereLayer.energyGenCurrentTick = 0L;
            var layerEnergyGenCurrentTick = 0L;
            var nodePool = dysonSphereLayer.nodePool;
            var shellPool = dysonSphereLayer.shellPool;
            for (var j = 1; j < dysonSphereLayer.nodeCursor; j++)
            {
                if (nodePool[j] != null && nodePool[j].id == j)
                {
                    layerEnergyGenCurrentTick += nodePool[j]
                        .EnergyGenCurrentTick((int)__instance.energyGenPerNode, (int)__instance.energyGenPerFrame);
                }
            }
            for (var k = 1; k < dysonSphereLayer.shellCursor; k++)
            {
                if (shellPool[k] != null && shellPool[k].id == k)
                {
                    layerEnergyGenCurrentTick += shellPool[k].cellPoint * __instance.energyGenPerShell;
                }
            }
            dysonSphereLayer.energyGenCurrentTick = layerEnergyGenCurrentTick;
            energyGenOriginalCurrentTick += layerEnergyGenCurrentTick;
            if (dysonSphereLayer.grossRadius > grossRadius)
            {
                grossRadius = dysonSphereLayer.grossRadius;
            }
        }
        __instance.grossRadius = grossRadius;
        __instance.energyGenOriginalCurrentTick = energyGenOriginalCurrentTick;
        // The coefficient is the host's, not the local property: this peer's hive shells carry no
        // gamma counts, so the vanilla getter would report no debuff at all.
        __instance.energyGenCurrentTick =
            (long)(energyGenOriginalCurrentTick * Multiplayer.Session.DysonSpheres.DebuffCoef(__instance.starData.index));
        // energyReqCurrentTick is deliberately left alone: vanilla would reset it to zero here and
        // let every planet's power network add its demand back, which only the host can total.
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
