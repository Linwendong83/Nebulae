#region

using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeModel.Packets.GameHistory;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UITechTree))]
internal class UITechTree_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UITechTree.Do1KeyUnlock))]
    public static bool Do1KeyUnlock_Prefix()
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.LocalPlayer.IsClient)
        {
            return true;
        }
        // Let host run one key unlock function
        Multiplayer.Session.Network.SendPacket(new GameHistoryNotificationPacket(GameHistoryEvent.OneKeyUnlock));
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UITechTree.OnPauseBtnClick))]
    public static bool OnPauseBtnClick_Prefix()
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
    [HarmonyPatch(nameof(UITechTree.OnPauseBtnClick))]
    public static void OnPauseBtnClick_Postfix()
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        SimulatedWorld.SyncWorldPauseAfterToggle();
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UITechTree._OnClose))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnClose_Postfix()
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        // Vanilla resumes the game when the tech tree closes; keep every peer in step
        SimulatedWorld.SyncWorldPauseAfterWindowClosed();
    }
}
