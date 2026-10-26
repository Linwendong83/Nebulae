#region

using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIDysonEditor))]
internal class UIDysonEditor_Patch
{
    // Gate the shared wrapper before it sets active or invokes any editor child panel.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ManualBehaviour), nameof(ManualBehaviour._Open))]
    public static bool Open_Prefix(ManualBehaviour __instance)
    {
        if (__instance is not UIDysonEditor || !__instance.inited || __instance.active ||
            !Multiplayer.IsActive || !Multiplayer.Session.LocalPlayer.IsClient) return true;
        var manager = Multiplayer.Session.DysonSpheres;
        var star = manager.GetEditorStar();
        if (star == null) return false;
        if (!manager.IsReady(star.index) || GameMain.data.dysonSpheres[star.index] == null)
        {
            manager.WaitForEditor(star.index, openWhenLoaded: true);
            return false;
        }
        Multiplayer.Session.Network.SendPacket(new DysonSphereLoadRequest(0, DysonSphereRequestEvent.List));
        return true;
    }

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
