#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic.UI;

[HarmonyPatch(typeof(UIGame))]
internal class UIGame_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIGame.dfVeinOnFinal), MethodType.Getter)]
    public static void DfVeinOnFinal_Postfix(ref bool __result)
    {
        // Preferences remember the player's choice across worlds. Gate the effective display
        // instead of clearing that choice, so research unlocks it without another reconnect.
        if (Multiplayer.IsActive && __result)
        {
            __result = GameMain.data?.history?.universeObserveLevel > 0 || GameMain.sandboxToolsEnabled;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIGame.StarmapChangingToMilkyWay))]
    public static bool StarmapChangingToMilkyWay_Prefix()
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }
        InGamePopup.ShowInfo("Unavailable".Translate(), "Milky Way is disabled in multiplayer game.".Translate(),
            "OK".Translate());
        return false;
    }
}
