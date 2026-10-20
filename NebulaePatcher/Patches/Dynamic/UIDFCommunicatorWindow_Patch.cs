using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIDFCommunicatorWindow))]
internal class UIDFCommunicatorWindow_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIDFCommunicatorWindow.OnTruceButtonClicked))]
    public static bool Truce_Prefix(UIDFCommunicatorWindow __instance)
    {
        if (!Multiplayer.IsActive) return true;
        Multiplayer.Session.PropertyTransactions.Request(MetadataOperation.Truce,
            PropertySystem.matrixIds[__instance.propertyIndex], __instance.countOpt[__instance.propertyIndex]);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIDFCommunicatorWindow.OnAggressiveIncButtonClicked))]
    public static bool Increase_Prefix()
    {
        if (!Multiplayer.IsActive) return true;
        Multiplayer.Session.PropertyTransactions.Request(MetadataOperation.IncreaseAggressiveness);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIDFCommunicatorWindow.OnAggressiveDecButtonClicked))]
    public static bool Decrease_Prefix()
    {
        if (!Multiplayer.IsActive) return true;
        Multiplayer.Session.PropertyTransactions.Request(MetadataOperation.DecreaseAggressiveness);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIDFCommunicatorWindow.WithdrawTruceConfirm))]
    public static bool Withdraw_Prefix()
    {
        if (!Multiplayer.IsActive) return true;
        Multiplayer.Session.PropertyTransactions.Request(MetadataOperation.WithdrawTruce);
        return false;
    }
}
