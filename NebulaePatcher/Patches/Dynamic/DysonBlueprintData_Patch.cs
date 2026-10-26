#region

using HarmonyLib;
using System;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(DysonBlueprintData))]
internal class DysonBlueprintData_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(DysonBlueprintData.FromBase64String))]
    public static void FromBase64String_Prefix(out bool __state)
    {
        __state = Multiplayer.Session?.DysonSpheres.InBlueprint ?? false;
        if (Multiplayer.IsActive)
        {
            Multiplayer.Session.DysonSpheres.InBlueprint = true;
        }
    }

#pragma warning disable Harmony003
    [HarmonyPostfix]
    [HarmonyPatch(nameof(DysonBlueprintData.FromBase64String))]
    public static void FromBase64String_Postfix(DysonBlueprintDataIOError __result, string str64Data,
        EDysonBlueprintType requestType, DysonSphere sphere, DysonSphereLayer layer, bool __state)
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        Multiplayer.Session.DysonSpheres.InBlueprint = __state;
        if (__state || Multiplayer.Session.DysonSpheres.IsIncomingRequest || __result != DysonBlueprintDataIOError.OK)
        {
            return;
        }
        var starIndex = sphere.starData.index;
        var layerId = layer?.id ?? -1;
        Multiplayer.Session.Network.SendPacket(new DysonBlueprintPacket(starIndex, layerId, requestType, str64Data));
    }

    [HarmonyFinalizer, HarmonyPatch(nameof(DysonBlueprintData.FromBase64String))]
    public static Exception FromBase64String_Finalizer(Exception __exception, bool __state)
    {
        if (Multiplayer.IsActive) Multiplayer.Session.DysonSpheres.InBlueprint = __state;
        return __exception;
    }
#pragma warning restore Harmony003
}
