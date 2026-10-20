#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIMechaLab))]
internal class UIMechaLab_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIMechaLab.OnSupplyButtonClick))]
    public static bool OnSupplyButtonClick_Prefix()
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.LocalPlayer.IsClient || GameMain.isFullscreenPaused ||
            !GameMain.history.autoManageLabItems)
        {
            return true;
        }

        // The vanilla warning reads the client's production-statistics history. Nebulae only synchronizes that
        // history while the statistics window is open, so use the authoritative state sent by the host instead.
        if (Multiplayer.Session.Statistics.HasActiveAutomaticResearch)
        {
            DisableAutomaticItemManagement();
            return false;
        }

        UIMessageBox.Show(Localization.Translate("取消研究背包物品标题"), Localization.Translate("取消研究背包物品提示"),
            Localization.Translate("取消"), Localization.Translate("确定"), 2, null, DisableAutomaticItemManagement);
        return false;
    }

    private static void DisableAutomaticItemManagement()
    {
        GameMain.history.autoManageLabItems = false;
    }
}
