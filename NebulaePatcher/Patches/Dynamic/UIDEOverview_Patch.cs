#region

using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIDEOverview))]
internal class UIDEOverview_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIDEOverview._OnUpdate))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnUpdate_Prefix(UIDEOverview __instance)
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        switch (__instance.autoConstructSwitch.isOn)
        {
            case true when !GameMain.data.history.HasFeatureKey(1100002):
                GameMain.data.history.RegFeatureKey(1100002);
                break;
            case false when GameMain.data.history.HasFeatureKey(1100002):
                GameMain.data.history.UnregFeatureKey(1100002);
                break;
        }
    }
}
