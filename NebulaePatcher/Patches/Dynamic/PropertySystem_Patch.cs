using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeWorld;
using NebulaeWorld.GameStates;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(PropertySystem))]
internal static class PropertySystem_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch("SaveToFile")]
    public static bool Save_Prefix(ref bool __result)
    {
        if (!Multiplayer.IsActive) return true;
        PropertyAccountStore.Save();
        __result = true;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PropertySystem.AddItemConsumption))]
    public static bool Consumption_Prefix()
    {
        // Multiplayer debits use the persisted transaction's absolute before/after values.
        return !Multiplayer.IsActive;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PropertySystem.RealizePropertyToInventory))]
    public static bool Realize_Prefix(int itemId, int count)
    {
        if (!Multiplayer.IsActive) return true;
        Multiplayer.Session.PropertyTransactions.Request(MetadataOperation.Matrix, itemId, count);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PropertySystem.Realize6006AsVariousMatrixToInventory))]
    public static bool Various_Prefix(int count)
    {
        if (!Multiplayer.IsActive) return true;
        Multiplayer.Session.PropertyTransactions.Request(MetadataOperation.VariousMatrices, 0, count);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PropertySystem.Realize6006AsOtherItemToInventory))]
    public static bool Other_Prefix(int itemId, int count)
    {
        if (!Multiplayer.IsActive) return true;
        Multiplayer.Session.PropertyTransactions.Request(MetadataOperation.DarkFogItems, itemId, count);
        return false;
    }
}

[HarmonyPatch(typeof(UIPropertyEntry), nameof(UIPropertyEntry.DoRealize))]
internal static class UIPropertyRealize_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(UIPropertyEntry __instance)
    {
        if (!Multiplayer.IsActive) return true;
        var mode = __instance.realizeMode;
        var target = mode switch
        {
            EPropertyRealizeMode.DFMatrix => 5201,
            EPropertyRealizeMode.EnergyFragment => 5206,
            EPropertyRealizeMode.VirtualParticle => 5205,
            EPropertyRealizeMode.VariousDF => 0,
            _ => __instance.itemId
        };
        var operation = mode == EPropertyRealizeMode.Default ? MetadataOperation.Matrix :
            mode == EPropertyRealizeMode.Various6006 ? MetadataOperation.VariousMatrices : MetadataOperation.DarkFogItems;
        Multiplayer.Session.PropertyTransactions.Request(operation, target, (int)__instance.countSlider.value);
        return false;
    }
}

[HarmonyPatch(typeof(GameHistoryData), nameof(GameHistoryData.BuyoutTech))]
internal static class BuyoutTech_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(int techId, ref bool __result)
    {
        if (!Multiplayer.IsActive) return true;
        Multiplayer.Session.PropertyTransactions.Request(MetadataOperation.BuyTech, techId);
        __result = false; // The authoritative unlock notification arrives after payment.
        return false;
    }
}
