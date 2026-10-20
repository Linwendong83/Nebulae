#region

using HarmonyLib;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(PlanetData))]
internal class PlanetData_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetData.NotifyLoaded))]
    public static void NotifyLoaded_Postfix(PlanetData __instance)
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Planets.TerrainLoaded(__instance.id);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetData.LoadFactory))]
    public static void LoadFactory_Prefix(PlanetData __instance)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.LocalPlayer.IsHost)
        {
            return;
        }
        // Stop packet processing for host until factory is loaded
        if (!__instance.factoryLoaded)
        {
            Multiplayer.Session.Planets.Loads.Begin(__instance.id, false);
            Multiplayer.Session.Planets.PausePackets(__instance.id);
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(nameof(PlanetData.LoadFactory))]
    public static System.Exception LoadFactory_Finalizer(PlanetData __instance, System.Exception __exception)
    {
        if (Multiplayer.IsActive && __exception != null)
            Multiplayer.Session.Planets.FailFactory(__instance, -1, __exception);
        return __exception;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetData.NotifyFactoryLoaded))]
    public static void NotifyFactoryLoaded_Prefix(PlanetData __instance)
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Planets.PrepareFactory(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetData.UpdateDirtyMesh))]
    public static bool UpdateDirtyMesh_Prefix(PlanetData __instance, int dirtyIdx, ref bool __result)
    {
        // Remote planets can mark terrain dirty before their render meshes are loaded.
        if (!__instance.dirtyFlags[dirtyIdx] || __instance.meshes[dirtyIdx] != null)
        {
            return true;
        }
        __result = false;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetData.UnloadMeshes))]
    public static bool UnloadMeshes_Prefix(PlanetData __instance)
    {
        //Host should not unload planet meshes, since he need to permorm all terrain operations
        if (!Multiplayer.IsActive || !Multiplayer.Session.LocalPlayer.IsHost)
        {
            return true;
        }
        //Do not unload meshes, just hide them so it is not visible
        UnloadVisuals(__instance);
        return false;

    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetData.UnloadData))]
    public static bool UnloadData_Prefix()
    {
        //Host should not unload planet data, since they need to perform all operations from users
        return !Multiplayer.IsActive || !Multiplayer.Session.LocalPlayer.IsHost;
    }

    private static void UnloadVisuals(PlanetData __instance)
    {
        if (__instance.gameObject != null)
        {
            __instance.gameObject.SetActive(false);
        }
        if (__instance.terrainMaterial != null)
        {
            Object.Destroy(__instance.terrainMaterial);
            __instance.terrainMaterial = null;
        }
        if (__instance.oceanMaterial != null)
        {
            Object.Destroy(__instance.oceanMaterial);
            __instance.oceanMaterial = null;
        }
        if (__instance.atmosMaterial != null)
        {
            Object.Destroy(__instance.atmosMaterial);
            __instance.atmosMaterial = null;
        }
        if (__instance.minimapMaterial != null)
        {
            Object.Destroy(__instance.minimapMaterial);
            __instance.minimapMaterial = null;
        }
        if (__instance.reformMaterial0 != null)
        {
            Object.Destroy(__instance.reformMaterial0);
            __instance.reformMaterial0 = null;
        }
        if (__instance.reformMaterial1 == null)
        {
            return;
        }
        Object.Destroy(__instance.reformMaterial1);
        __instance.reformMaterial1 = null;
    }
}
