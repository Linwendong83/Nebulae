using HarmonyLib;
using NebulaeWorld.GameStates;
using NebulaeModel.Logger;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch]
internal class UIDashboard_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIDashboard), nameof(UIDashboard._OnClose))]
    public static void UIDashboard_OnClose_Postfix(UIDashboard __instance)
    {
        try { __instance.CollectStates(); }
        catch (System.Exception e) { Log.Warn("Failed to collect dashboard preferences: " + e.Message); }
        LocalPlayerPreferences.SaveDashboard();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CustomCharts), nameof(CustomCharts.CreateOrFindStatPlan))]
    public static void SaveDashboard_Postfix(bool isCreated)
    {
        if (isCreated) LocalPlayerPreferences.SaveDashboard();
    }

    [HarmonyPostfix, HarmonyPatch(typeof(CustomCharts), nameof(CustomCharts.RemoveStatPlan))]
    public static void RemoveDashboard_Postfix() => LocalPlayerPreferences.SaveDashboard();
}
