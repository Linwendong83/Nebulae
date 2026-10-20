using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(UILabWindow))]
internal static class LaboratoryInteraction_Patch
{
    private static readonly AccessTools.FieldRef<UILabWindow, PlanetFactory> Factory = AccessTools.FieldRefAccess<UILabWindow, PlanetFactory>("factory");
    private static readonly AccessTools.FieldRef<UILabWindow, global::Player> Player = AccessTools.FieldRefAccess<UILabWindow, global::Player>("player");
    private static readonly AccessTools.FieldRef<UILabWindow, RecipeProto[]> Recipes = AccessTools.FieldRefAccess<UILabWindow, RecipeProto[]>("matrixRecipes");
    [HarmonyPrefix, HarmonyPatch("OnItemButtonClick")]
    internal static bool Item(UILabWindow __instance, int index)
    {
        if (!Multiplayer.IsActive) return true;
        var window = __instance; var id = -window.labId;
        if (Factory(window) == null || window.labId <= 0 || index < 0) return false;
        var machine = new NebulaeWorld.Factory.FactoryMachine(Factory(window), id).View;
        if (machine.recipeId == 0)
        {
            if (Player(window).inhandItemId > 0 || index >= LabComponent.matrixIds.Length) return false;
            var recipe = Recipes(window)?[index];
            if (recipe != null) Recipe(window, recipe.ID);
        }
        else if (Player(window).inhandItemCount > 0)
        {
            if (Multiplayer.Session.IsClient) Multiplayer.Session.FactoryProducts.RequestHandInput(Factory(window), id, index, 0);
            else Multiplayer.Session.FactoryProducts.LocalHandInput(Factory(window), id, index, 0, Player(window));
        }
        else
        {
            var hand = !VFInput.shift && !VFInput.control;
            if (Multiplayer.Session.IsClient) Multiplayer.Session.FactoryProducts.RequestInputWithdrawal(Factory(window), id, index, 0, hand);
            else Multiplayer.Session.FactoryProducts.LocalInputWithdrawal(Factory(window), id, index, 0, hand, Player(window));
        }
        return false;
    }
    [HarmonyPrefix, HarmonyPatch("OnProductButtonClick")]
    internal static bool Product(UILabWindow __instance)
    {
        if (!Multiplayer.IsActive) return true;
        if (Factory(__instance) == null || __instance.labId <= 0 || Player(__instance).inhandItemId > 0) return false;
        var lab = Factory(__instance).factorySystem.labPool[__instance.labId];
        if (lab.researchMode) return true;
        if (lab.matrixMode)
        {
            if (Multiplayer.Session.IsClient) Multiplayer.Session.FactoryProducts.Request(Factory(__instance), -__instance.labId, 0);
            else Multiplayer.Session.FactoryProducts.LocalWithdraw(Factory(__instance), -__instance.labId, 0, Player(__instance));
        }
        else Recipe(__instance, -1);
        return false;
    }
    [HarmonyPrefix, HarmonyPatch("OnBackButtonClick")]
    internal static bool Reset(UILabWindow __instance)
    {
        if (!Multiplayer.IsActive) return true;
        Recipe(__instance, 0); return false;
    }
    private static void Recipe(UILabWindow window, int recipe)
    {
        if (Factory(window) == null || window.labId <= 0) return;
        if (Multiplayer.Session.IsClient) Multiplayer.Session.FactoryProducts.RequestRecipe(Factory(window), -window.labId, recipe);
        else if (!Multiplayer.Session.FactoryProducts.LocalRecipe(Factory(window), -window.labId, recipe, Player(window).package)) UIRealtimeTip.Popup("背包空间不足".Translate());
    }
    [HarmonyPrefix, HarmonyPatch("OnIncSwitchClick")]
    internal static bool Acceleration(UILabWindow __instance)
    {
        if (!Multiplayer.IsActive) return true;
        if (Factory(__instance) == null || __instance.labId <= 0) return false;
        var lab = Factory(__instance).factorySystem.labPool[__instance.labId];
        if (!lab.matrixMode) return false;
        if (Multiplayer.Session.IsClient) Multiplayer.Session.FactoryProducts.RequestAcceleration(Factory(__instance), -__instance.labId, !lab.forceAccMode);
        else Multiplayer.Session.FactoryProducts.LocalAcceleration(Factory(__instance), -__instance.labId, !lab.forceAccMode);
        return false;
    }
}
