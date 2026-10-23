using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(VegetableCollection))]
internal static class VegetableCollection_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(VegetableCollection.AddVegeToPlayer))]
    [HarmonyPatch(nameof(VegetableCollection.RemoveVegeFromPlayer))]
    [HarmonyPatch(nameof(VegetableCollection.TransferVegeToPlayer))]
    [HarmonyPatch(nameof(VegetableCollection.AddVege), typeof(int), typeof(EObjectType), typeof(int), typeof(int), typeof(int))]
    public static void BeforeCollectionChanged(VegetableCollection __instance) =>
        Multiplayer.Session?.Vegetation?.BeforeLocalChange(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(VegetableCollection.AddVegeToPlayer))]
    [HarmonyPatch(nameof(VegetableCollection.RemoveVegeFromPlayer))]
    [HarmonyPatch(nameof(VegetableCollection.TransferVegeToPlayer))]
    public static void PlayerCollectionChanged(VegetableCollection __instance) =>
        Multiplayer.Session?.Vegetation?.MarkLocalDirty(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(VegetableCollection.AddVege), typeof(int), typeof(EObjectType), typeof(int), typeof(int), typeof(int))]
    public static void OwnerCollectionChanged(VegetableCollection __instance) =>
        Multiplayer.Session?.Vegetation?.MarkLocalDirty(__instance);
}
