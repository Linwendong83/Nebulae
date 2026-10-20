#region

using HarmonyLib;
using NebulaeModel.Authority;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIMechaWindow))]
internal class UIMechaWindow_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIMechaWindow.OnReplaceFuelButtonClick))]
    public static bool OnReplaceFuelButtonClick_Prefix()
    {
        return AuthorityRuleGuard.AllowHostRule();
    }
}
