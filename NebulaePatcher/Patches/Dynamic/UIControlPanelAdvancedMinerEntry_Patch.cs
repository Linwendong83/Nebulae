#region

using HarmonyLib;

using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIControlPanelAdvancedMinerEntry))]
internal class UIControlPanelAdvancedMinerEntry_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelAdvancedMinerEntry.OnSetTarget))]
    public static bool OnSetTarget_Prefix(UIControlPanelAdvancedMinerEntry __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        var factory = GameMain.data.galaxy.PlanetById(__instance.target.astroId).factory;
        if (factory == null)
        {
            __instance.station = Multiplayer.Session.StationsUI.DummyStationStoreContainer;
            __instance.storageItem.station = __instance.station;
            __instance.storageItem.SetVisible(false);
            return false;
        }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelAdvancedMinerEntry._OnUpdate))]
    public static bool Update_Prefix(UIControlPanelAdvancedMinerEntry __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        if (__instance.factory != null) return true;

        UIControlPanelObjectEntry_Patch.OnUpdate(__instance);
        __instance.viewToTargetButton.button.interactable = __instance.isLocal;
        // Visible remote rows are subscribed together by the parent window.
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelAdvancedMinerEntry.isLocal), MethodType.Getter)]
    public static bool IsLocal_Prefix(UIControlPanelAdvancedMinerEntry __instance, ref bool __result)
    {
        if (__instance.factory == null)
        {
            __result = false;
            return false;
        }
        return true;
    }
}
