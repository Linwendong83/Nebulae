#region

using System;
using HarmonyLib;
using NebulaeModel.Packets.Statistics;
using NebulaeWorld;
#pragma warning disable IDE0301 // Simplify collection initialization

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIReferenceSpeedTip))]
internal class UIReferenceSpeedTip_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIReferenceSpeedTip.AddEntryDataWithFactory))]
    public static bool AddEntryDataWithFactory_Prefix(UIReferenceSpeedTip __instance)
    {
        // Client will use server response to update loadedEntryDatas and loadedSubTipDatas
        return !Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost || Multiplayer.Session.Statistics.LocalReferences.CanCalculate(__instance.astroFilter);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIReferenceSpeedTip.SetTip))]
    public static void SetTip_Prefix(UIReferenceSpeedTip __instance, int _itemId, int _astroFilter, UIReferenceSpeedTip.EItemCycle _itemCycle)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost) return;

        // Client: Send request to server when setting a new tip
        if (__instance.itemId == _itemId && __instance.astroFilter == _astroFilter && __instance.itemCycle == _itemCycle) return;
        var local = Multiplayer.Session.Statistics.LocalReferences;
        if (local.CanCalculate(_astroFilter) || local.HasTip(_itemId, _astroFilter, (int)_itemCycle, 0)) return;
        Multiplayer.Session.Network.SendPacket(new StatisticsReferenceSpeedTipPacket(
            _itemId, _astroFilter, (int)_itemCycle, 0, Array.Empty<byte>()));
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIReferenceSpeedTip.SetSubTip))]
    public static void SetSubTip_Prefix(UIReferenceSpeedTip __instance, int _productionProtoId)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost) return;

        // Client: Send request to server when setting a valid subtip
        if (_productionProtoId == 0) return;
        var local = Multiplayer.Session.Statistics.LocalReferences;
        if (local.CanCalculate(__instance.astroFilter) || local.HasTip(__instance.itemId, __instance.astroFilter, (int)__instance.itemCycle, _productionProtoId)) return;
        Multiplayer.Session.Network.SendPacket(new StatisticsReferenceSpeedTipPacket(
            __instance.itemId, __instance.astroFilter, (int)__instance.itemCycle, _productionProtoId, Array.Empty<byte>()));
    }

    [HarmonyPostfix, HarmonyPatch(nameof(UIReferenceSpeedTip.SetTip))]
    public static void Tip_Postfix(UIReferenceSpeedTip __instance)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient && !Multiplayer.Session.Statistics.LocalReferences.CanCalculate(__instance.astroFilter))
            Multiplayer.Session.Statistics.LocalReferences.ApplyTip(__instance, 0);
    }

    [HarmonyPostfix, HarmonyPatch(nameof(UIReferenceSpeedTip.SetSubTip))]
    public static void SubTip_Postfix(UIReferenceSpeedTip __instance, int _productionProtoId)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient && !Multiplayer.Session.Statistics.LocalReferences.CanCalculate(__instance.astroFilter))
            Multiplayer.Session.Statistics.LocalReferences.ApplyTip(__instance, _productionProtoId);
    }
}
