#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIFractionatorWindow))]
internal class UIFractionatorWindow_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIFractionatorWindow.OnProductUIButtonClick))]
    public static bool Product(UIFractionatorWindow __instance, int itemId)
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }
        if (__instance.factory == null || __instance.fractionatorId <= 0) return false;
        var fractionator = __instance.factorySystem.fractionatorPool[__instance.fractionatorId];
        var slot = itemId == fractionator.productId ? 0 : itemId == fractionator.fluidId ? 1 : -1;
        if (slot < 0) return false;
        if (__instance.player.inhandItemId > 0 && __instance.player.inhandItemCount == 0) { __instance.player.SetHandItems(0, 0); return false; }
        var deposit = __instance.player.inhandItemCount > 0;
        var hand = deposit || !VFInput.control && !VFInput.shift;
        if (Multiplayer.Session.IsClient) Multiplayer.Session.FactoryProducts.RequestFractionator(__instance.factory, __instance.fractionatorId, slot, hand, deposit);
        else Multiplayer.Session.FactoryProducts.LocalFractionator(__instance.factory, __instance.fractionatorId, slot, __instance.player, hand, deposit);
        return false;
    }
}
