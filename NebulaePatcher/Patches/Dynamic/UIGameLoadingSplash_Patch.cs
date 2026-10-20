using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIGameLoadingSplash), nameof(UIGameLoadingSplash.OnEnable))]
internal static class UIGameLoadingSplash_Patch
{
    [HarmonyPostfix]
    private static void OnEnable_Postfix(UIGameLoadingSplash __instance)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient)
            Multiplayer.Session.JoinSync.RefreshLoadingPrompt(__instance);
    }
}
