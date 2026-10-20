#region

using HarmonyLib;
using NebulaeModel;
using NebulaeModel.Logger;
using NebulaeNetwork;
using NebulaeWorld;
using NebulaeWorld.GameStates;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UILoadGameWindow))]
internal class UILoadGameWindow_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UILoadGameWindow.OnCancelClick))]
    public static void OnCancelClick_Postfix()
    {
        MultiplayerPage.ReturnFromLoadWindow();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UILoadGameWindow.DoLoadSelectedGame))]
    public static bool DoLoadSelectedGame_Prefix()
    {
        if (Multiplayer.IsInMultiplayerMenu)
            GoalManager.PendingHostLevel = UIRoot.instance.loadGameWindow.loadWithGoalLevel;
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient) return false;
        if (Multiplayer.Session.IsGameLoaded)
        {
            Multiplayer.LeaveGame();
            Multiplayer.IsInMultiplayerMenu = true;
        }
        return true;
    }
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UILoadGameWindow.DoLoadSelectedGame))]
    public static void DoLoadSelectedGame_Postfix()
    {
        if (!Multiplayer.IsInMultiplayerMenu)
        {
            return;
        }
        MultiplayerPage.LoadSucceeded();
        Log.Info($"Listening server on port {Config.Options.HostPort}");
        Multiplayer.HostGame(new Server(Config.Options.HostPort, true));
    }
}
