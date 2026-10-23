using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class FighterPortInteraction_Patch
{
    [HarmonyPostfix, HarmonyPatch(typeof(UIBattleBaseWindow), nameof(UIBattleBaseWindow.OnBattleBaseIdChange))]
    public static void ObserveBase(UIBattleBaseWindow __instance)
    {
        if (Multiplayer.IsActive && __instance.factory != null && __instance.battleBase?.id == __instance.battleBaseId)
            Multiplayer.Session.FactoryProducts.ObserveFighterPorts(__instance.factory, __instance.battleBase.entityId);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(UIBattleBaseFighterEntry), nameof(UIBattleBaseFighterEntry.OnFighterButtonClick))]
    public static bool BaseLeft(UIBattleBaseFighterEntry __instance)
    {
        if (!Multiplayer.IsActive) return true;
        if (__instance.player.inhandItemCount == 0 && __instance.fighter.count == 0 && !VFInput.control) return true;
        return Base(__instance, FighterPortOperation.Left | (VFInput.control ? FighterPortOperation.Control : 0));
    }
    [HarmonyPrefix, HarmonyPatch(typeof(UIBattleBaseFighterEntry), nameof(UIBattleBaseFighterEntry.OnFighterButtonRightClick))]
    public static bool BaseRight(UIBattleBaseFighterEntry __instance) => !Multiplayer.IsActive || Base(__instance, FighterPortOperation.Right);

    [HarmonyPrefix, HarmonyPatch(typeof(UIBattleBaseFighterEntry), nameof(UIBattleBaseFighterEntry.OnPortTypeButtonClick))]
    public static bool BaseConfigure(UIBattleBaseFighterEntry __instance, int obj)
    {
        if (!Multiplayer.IsActive) return true;
        Base(__instance, FighterPortOperation.Configure, obj);
        __instance.ClosePortConfigPanel();
        return false;
    }
    private static bool Base(UIBattleBaseFighterEntry entry, int action, int item = 0)
    {
        var factory = entry.factory;
        var id = entry.battleBase?.entityId ?? 0;
        if (factory == null || id <= 0) return false;
        var manager = Multiplayer.Session.FactoryProducts;
        if (Multiplayer.Session.IsClient) manager.RequestFighterPort(factory, id, 0, entry.fighterIndex, false, action, item);
        else if (!manager.LocalFighterPort(factory, id, 0, entry.fighterIndex, false, action, item, entry.player))
            UIRealtimeTip.Popup("无法完成操作".Translate());
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(UIMechaFighterEntry), nameof(UIMechaFighterEntry.OnFighterButtonClick))]
    public static bool MechaLeft(UIMechaFighterEntry __instance)
    {
        if (!Multiplayer.IsActive) return true;
        if (__instance.player.inhandItemCount == 0 && __instance.fighter.count == 0 && !VFInput.control) return true;
        return Mecha(__instance, FighterPortOperation.Left | (VFInput.control ? FighterPortOperation.Control : 0));
    }
    [HarmonyPrefix, HarmonyPatch(typeof(UIMechaFighterEntry), nameof(UIMechaFighterEntry.OnFighterButtonRightClick))]
    public static bool MechaRight(UIMechaFighterEntry __instance) => !Multiplayer.IsActive || Mecha(__instance, FighterPortOperation.Right);

    [HarmonyPrefix, HarmonyPatch(typeof(UIMechaFighterEntry), nameof(UIMechaFighterEntry.OnPortTypeButtonClick))]
    public static bool MechaConfigure(UIMechaFighterEntry __instance, int obj)
    {
        if (!Multiplayer.IsActive) return true;
        Mecha(__instance, FighterPortOperation.Configure, obj);
        __instance.ClosePortConfigPanel();
        return false;
    }
    private static bool Mecha(UIMechaFighterEntry entry, int action, int item = 0)
    {
        var manager = Multiplayer.Session.FactoryProducts;
        if (Multiplayer.Session.IsClient) manager.RequestFighterPort(null, 0, entry.fleetIndex, entry.fighterIndex, entry.isSpace, action, item);
        else if (!manager.LocalFighterPort(null, 0, entry.fleetIndex, entry.fighterIndex, entry.isSpace, action, item, entry.player))
            UIRealtimeTip.Popup("无法完成操作".Translate());
        return false;
    }
}
