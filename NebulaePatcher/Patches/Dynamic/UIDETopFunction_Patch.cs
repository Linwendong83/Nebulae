#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIDETopFunction))]
internal class UIDETopFunction_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIDETopFunction.OnPauseButtonClick))]
    public static bool OnPauseButtonClick_Prefix()
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.LocalPlayer.IsClient)
        {
            return true;
        }
        // The pause flag is owned by the host in multiplayer: ask instead of writing it
        SimulatedWorld.RequestWorldPause();
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIDETopFunction.OnPauseButtonClick))]
    public static void OnPauseButtonClick_Postfix()
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        SimulatedWorld.SyncWorldPauseAfterToggle();
    }
}
