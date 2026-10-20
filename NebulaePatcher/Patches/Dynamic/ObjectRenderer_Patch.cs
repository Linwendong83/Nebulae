using NebulaeModel.Networking;
#region

using HarmonyLib;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(ObjectRenderer))]
internal class ObjectRenderer_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ObjectRenderer), nameof(ObjectRenderer.AddInst), typeof(int), typeof(Vector3), typeof(Quaternion),
        typeof(bool))]
    [HarmonyPatch(typeof(ObjectRenderer), nameof(ObjectRenderer.AddInst), typeof(int), typeof(Vector3), typeof(Quaternion),
        typeof(uint), typeof(bool))]
    public static bool AddInst_Prefix()
    {
        //Do not call renderer, if user is not on the planet as the request
        return !Multiplayer.IsActive || Multiplayer.Session.Factories.TargetPlanet == SessionSentinels.PLANET_NONE ||
               GameMain.mainPlayer.planetId == Multiplayer.Session.Factories.TargetPlanet;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ObjectRenderer.AlterInst), typeof(int), typeof(int), typeof(Vector3), typeof(Quaternion),
        typeof(bool))]
    public static bool AlterInst_Prefix()
    {
        //Do not call renderer, if user is not on the planet as the request
        return !Multiplayer.IsActive || Multiplayer.Session.Factories.TargetPlanet == SessionSentinels.PLANET_NONE ||
               GameMain.mainPlayer.planetId == Multiplayer.Session.Factories.TargetPlanet;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ObjectRenderer.AlterInst), typeof(int), typeof(int), typeof(Vector3), typeof(bool))]
    public static bool AlterInst_Prefix2()
    {
        //Do not call renderer, if user is not on the planet as the request
        return !Multiplayer.IsActive || Multiplayer.Session.Factories.TargetPlanet == SessionSentinels.PLANET_NONE ||
               GameMain.mainPlayer.planetId == Multiplayer.Session.Factories.TargetPlanet;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ObjectRenderer.RemoveInst))]
    public static bool RemoveInst_Prefix()
    {
        //Do not call renderer, if user is not on the planet as the request
        return !Multiplayer.IsActive || Multiplayer.Session.Factories.TargetPlanet == SessionSentinels.PLANET_NONE ||
               GameMain.mainPlayer.planetId == Multiplayer.Session.Factories.TargetPlanet;
    }
}
