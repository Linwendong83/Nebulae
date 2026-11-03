#region

using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.GameStates;

#endregion

namespace NebulaePatcher.Patches.Dynamic.UI;

[HarmonyPatch(typeof(UIReplicatorWindow))]
internal class UIReplicatorWindow_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIReplicatorWindow._OnUpdate))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnUpdate_Postfix(UIReplicatorWindow __instance)
    {
        // Both sandbox switches are written into GamePrefsData, which only a native world save
        // persists. Remember a client's personal choice for this world as soon as it changes.
        if (!Multiplayer.IsActive || !GameMain.sandboxToolsEnabled) return;
        LocalPlayerPreferences.SaveSandboxReplicatorPreferences(__instance.isInstantItem, __instance.isBatch);
    }
}
