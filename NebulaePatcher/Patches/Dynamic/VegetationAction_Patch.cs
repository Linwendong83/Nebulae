using HarmonyLib;
using NebulaeWorld;
using System;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch]
internal static class VegetationAction_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerAction_Mine), nameof(PlayerAction_Mine.GameTick))]
    [HarmonyPatch(typeof(PlayerAction_Plant), nameof(PlayerAction_Plant.RemoveAction))]
    public static void CollectPrefix()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Vegetation.EnterDirectAction();
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(PlayerAction_Mine), nameof(PlayerAction_Mine.GameTick))]
    [HarmonyPatch(typeof(PlayerAction_Plant), nameof(PlayerAction_Plant.RemoveAction))]
    public static void CollectFinalizer()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Vegetation.LeaveDirectAction();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerAction_Plant), nameof(PlayerAction_Plant.PlantVegeFinally))]
    public static void PlantPrefix()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Vegetation.EnterPlanting();
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(PlayerAction_Plant), nameof(PlayerAction_Plant.PlantVegeFinally))]
    public static void PlantFinalizer()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Vegetation.LeavePlanting();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.BuildFinally))]
    [HarmonyPatch(typeof(PlayerAction_Build), nameof(PlayerAction_Build.DoDismantleObject))]
    public static void BuildPrefix()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Vegetation.EnterBulkAction();
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.BuildFinally))]
    [HarmonyPatch(typeof(PlayerAction_Build), nameof(PlayerAction_Build.DoDismantleObject))]
    public static void BuildFinalizer()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Vegetation.LeaveBulkAction();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.DetermineReforms))]
    public static void BlueprintPrefix()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Vegetation.EnterBulkAction();
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.DetermineReforms))]
    public static void BlueprintFinalizer()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Vegetation.LeaveBulkAction();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.SetVegetationCollectionActive))]
    public static void LegacyCollectionActivationPrefix(out bool __state)
    {
        __state = Multiplayer.IsActive && Multiplayer.Session.IsClient &&
                  Multiplayer.Session.Vegetation.ReplayCollection == null;
        if (__state) Multiplayer.Session.Vegetation.BeginReplay(Array.Empty<byte>());
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.SetVegetationCollectionActive))]
    public static void LegacyCollectionActivationFinalizer(bool __state)
    {
        if (__state) Multiplayer.Session.Vegetation.EndReplay();
    }
}
