using HarmonyLib;
using NebulaeModel.Utils;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(Localization), nameof(Localization.Translate), typeof(string))]
internal static class Localization_Patch
{
    [HarmonyPrefix]
    private static bool Translate_Prefix(string __0, ref string __result)
    {
        if (!Localization.Loaded || !NebulaeLocalization.TryTranslate(__0, Localization.isZHCN, out var translation))
        {
            return true;
        }
        __result = translation;
        return false;
    }
}
