#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(DESelection))]
internal class DESelection_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(DESelection.SetViewStar))]
    public static bool SetViewStar_Prefix(DESelection __instance, ref StarData starData)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.LocalPlayer.IsClient)
        {
            return true;
        }
        var manager = Multiplayer.Session.DysonSpheres;
        var editor = UIRoot.instance?.uiGame?.dysonEditor;
        if (editor?.sceneGroup?.activeSelf == false && GameMain.localStar == null)
            starData = manager.GetEditorStar();
        if (starData == null || manager.IsReady(starData.index) && GameMain.data.dysonSpheres[starData.index] != null)
        {
            return true;
        }
        manager.WaitForEditor(starData.index, openWhenLoaded: false);
        // Restore comboBox back to original star
        var dysonBox = editor?.controlPanel?.topFunction?.dysonBox;
        if (dysonBox != null)
        {
            var index = dysonBox.ItemsData.FindIndex(x => x == __instance.viewStar?.index);
            if (index >= 0) dysonBox.itemIndex = index;
        }
        return false;
    }
}
