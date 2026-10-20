#region

using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIDysonEditor))]
internal class UIDysonEditor_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIDysonEditor._OnClose))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnClose_Postfix()
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        // Vanilla resumes the game when the editor closes; keep every peer in step
        SimulatedWorld.SyncWorldPauseAfterWindowClosed();
    }
}
