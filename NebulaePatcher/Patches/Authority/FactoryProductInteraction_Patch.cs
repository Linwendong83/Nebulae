using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class FactoryProductInteraction_Patch
{
    [HarmonyPrefix, HarmonyPatch(typeof(UIAssemblerWindow), nameof(UIAssemblerWindow.OnProductIcon0Click))]
    private static bool Product0(UIAssemblerWindow __instance) => Product(__instance, 0);

    [HarmonyPrefix, HarmonyPatch(typeof(UIAssemblerWindow), nameof(UIAssemblerWindow.OnProductIcon1Click))]
    private static bool Product1(UIAssemblerWindow __instance) => Product(__instance, 1);

    [HarmonyPrefix, HarmonyPatch(typeof(UIAssemblerWindow), nameof(UIAssemblerWindow.OnRecipeResetClick))]
    private static bool Reset(UIAssemblerWindow __instance) => Recipe(__instance, 0);

    [HarmonyPrefix, HarmonyPatch(typeof(UIAssemblerWindow), nameof(UIAssemblerWindow.OnRecipePickerReturn))]
    private static bool Select(UIAssemblerWindow __instance, RecipeProto recipe) => Recipe(__instance, recipe?.ID ?? 0);

    private static bool Recipe(UIAssemblerWindow window, int nextRecipe)
    {
        if (!Multiplayer.IsActive) return true;
        if (window.factory == null || window.assemblerId <= 0) return false;
        if (Multiplayer.Session.IsClient) Multiplayer.Session.FactoryProducts.RequestRecipe(window.factory, window.assemblerId, nextRecipe);
        else if (!Multiplayer.Session.FactoryProducts.LocalRecipe(window.factory, window.assemblerId, nextRecipe, window.player.package)) UIRealtimeTip.Popup("背包空间不足".Translate());
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(UIAssemblerWindow), nameof(UIAssemblerWindow.OnIncSwitchClick))]
    private static bool Acceleration(UIAssemblerWindow __instance)
    {
        if (!Multiplayer.IsActive) return true;
        if (__instance.factory == null || __instance.assemblerId <= 0) return false;
        var enable = !__instance.factorySystem.assemblerPool[__instance.assemblerId].forceAccMode;
        if (Multiplayer.Session.IsClient) Multiplayer.Session.FactoryProducts.RequestAcceleration(__instance.factory, __instance.assemblerId, enable);
        else Multiplayer.Session.FactoryProducts.LocalAcceleration(__instance.factory, __instance.assemblerId, enable);
        return false;
    }

    private static bool Product(UIAssemblerWindow window, int slot)
    {
        if (!Multiplayer.IsActive) return true;
        if (window.factory == null || window.assemblerId <= 0 || window.player.inhandItemId > 0) return false;
        if (Multiplayer.Session.IsClient) Multiplayer.Session.FactoryProducts.Request(window.factory, window.assemblerId, slot);
        else Multiplayer.Session.FactoryProducts.LocalWithdraw(window.factory, window.assemblerId, slot, window.player);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.EntityFastTakeOut))]
    private static bool FastTake(PlanetFactory __instance, int entityId, bool toPackage, ref ItemBundle itemBundle, ref bool full)
    {
        if (!Multiplayer.IsActive || entityId <= 0 || entityId >= __instance.entityCursor || __instance.entityPool[entityId].id != entityId) return true;
        var entity = __instance.entityPool[entityId]; var assembler = entity.assemblerId > 0 ? entity.assemblerId : -entity.labId;
        if (entity.ejectorId > 0 || entity.siloId > 0)
        {
            itemBundle = new ItemBundle(); full = false;
            if (Multiplayer.Session.IsClient) Multiplayer.Session.FactoryProducts.RequestLauncher(__instance, entityId, false, !toPackage);
            else
            {
                var item = entity.ejectorId > 0 ? __instance.factorySystem.ejectorPool[entity.ejectorId].bulletId : __instance.factorySystem.siloPool[entity.siloId].bulletId;
                var added = Multiplayer.Session.FactoryProducts.LocalLauncher(__instance, entityId, false, !toPackage, GameMain.mainPlayer);
                if (added > 0) itemBundle.Alter(item, added);
                full = (entity.ejectorId > 0 ? __instance.factorySystem.ejectorPool[entity.ejectorId].bulletCount : __instance.factorySystem.siloPool[entity.siloId].bulletCount) > 0;
            }
            return false;
        }
        if (entity.fractionatorId > 0)
        {
            itemBundle = new ItemBundle(); full = false;
            var fractionator = __instance.factorySystem.fractionatorPool[entity.fractionatorId];
            if (Multiplayer.Session.IsClient) Multiplayer.Session.FactoryProducts.RequestFractionator(__instance, entity.fractionatorId, 0, !toPackage);
            else
            {
                var added = Multiplayer.Session.FactoryProducts.LocalFractionator(__instance, entity.fractionatorId, 0, GameMain.mainPlayer, !toPackage);
                if (added > 0) itemBundle.Alter(fractionator.productId, added);
                if (__instance.factorySystem.fractionatorPool[entity.fractionatorId].productOutputCount > 0) full = true;
            }
            if (fractionator.belt0 > 0) __instance.BeltFastTakeOut(fractionator.belt0, toPackage, true, 0, ref full);
            return false;
        }
        var binding = new NebulaeWorld.Factory.FactoryMachine(__instance, assembler);
        if (!binding.Valid) return true;
        var produced = binding.View.produced;
        itemBundle = new ItemBundle(); full = false;
        if (produced == null) return false;
        for (var slot = 0; slot < produced.Length; slot++)
            if (Multiplayer.Session.IsClient) Multiplayer.Session.FactoryProducts.Request(__instance, assembler, slot, !toPackage);
            else
            {
                var machine = binding.View;
                var added = Multiplayer.Session.FactoryProducts.LocalWithdraw(__instance, assembler, slot, GameMain.mainPlayer, false, !toPackage);
                if (added > 0) itemBundle.Alter(machine.recipeExecuteData.products[slot], added);
                if (produced[slot] > 0) full = true;
            }
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.EntityFastFillIn))]
    private static bool FastFill(PlanetFactory __instance, int entityId, bool fromPackage, ref ItemBundle itemBundle)
    {
        if (!Multiplayer.IsActive || entityId <= 0 || entityId >= __instance.entityCursor || __instance.entityPool[entityId].id != entityId) return true;
        var entity = __instance.entityPool[entityId]; var assembler = entity.assemblerId > 0 ? entity.assemblerId : -entity.labId;
        if (entity.ejectorId > 0 || entity.siloId > 0)
        {
            itemBundle = new ItemBundle();
            if (Multiplayer.Session.IsClient) Multiplayer.Session.FactoryProducts.RequestLauncher(__instance, entityId, true, !fromPackage);
            else
            {
                var item = entity.ejectorId > 0 ? __instance.factorySystem.ejectorPool[entity.ejectorId].bulletId : __instance.factorySystem.siloPool[entity.siloId].bulletId;
                var added = Multiplayer.Session.FactoryProducts.LocalLauncher(__instance, entityId, true, !fromPackage, GameMain.mainPlayer);
                if (added > 0) itemBundle.Alter(item, -added);
            }
            return false;
        }
        var binding = new NebulaeWorld.Factory.FactoryMachine(__instance, assembler);
        if (!binding.Valid) return true;
        itemBundle = new ItemBundle();
        var requires = binding.View.recipeExecuteData?.requires;
        if (requires == null) return false;
        for (var slot = 0; slot < requires.Length; slot++)
        {
            if (binding.View.recipeId == -1 && !GameMain.history.ItemUnlocked(requires[slot])) continue;
            if (Multiplayer.Session.IsClient)
            {
                if (fromPackage) Multiplayer.Session.FactoryProducts.RequestInput(__instance, assembler, slot);
                else Multiplayer.Session.FactoryProducts.RequestHandInput(__instance, assembler, slot, 0);
            }
            else
            {
                var count = fromPackage ? Multiplayer.Session.FactoryProducts.LocalInput(__instance, assembler, slot, GameMain.mainPlayer.package)
                    : Multiplayer.Session.FactoryProducts.LocalHandInput(__instance, assembler, slot, 0, GameMain.mainPlayer);
                if (count > 0) itemBundle.Alter(requires[slot], -count);
            }
        }
        return false;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.Import))]
    private static void Imported(PlanetFactory __instance)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient) Multiplayer.Session.FactoryProducts.FactoryImported(__instance.planetId);
    }
}
