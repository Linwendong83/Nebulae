#region

using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeModel.Packets.GameStates;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(GameMain))]
internal class GameMain_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GameMain.HandleApplicationQuit))]
    public static void HandleApplicationQuit_Prefix()
    {
        if (Multiplayer.IsActive)
        {
            Log.Warn("Multiplayer is still running, closing now...");
            Multiplayer.LeaveGame();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(GameMain.DetermineGameTickRate))]
    public static void DetermineGameTickRate_Postfix(ref int __result)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.WorldPaused)
        {
            __result = Multiplayer.Session.JoinSync?.CanSimulate != false &&
                GameMain.gameTick < Multiplayer.Session.WorldPauseStepTick ? 1 : 0;
            return;
        }
        // Once the local baseline exists, execute real steps while join synchronization runs to
        // catch the host clock. Holding those steps would deadlock tick-stamped replica tails.
        if (Multiplayer.Session?.JoinSync?.LocalPending == true)
        {
            __result = Multiplayer.Session.JoinSync.CanSimulate && !Multiplayer.Session.WorldPaused ? 1 : 0;
            return;
        }
        // Ordinary menus do not pause a running multiplayer world.
        if (Multiplayer.Session != null && Multiplayer.Session.CanPause == false && Multiplayer.Session.WorldPaused == false)
        {
            __result = 1;
        }
    }

    [HarmonyPrefix, HarmonyPatch(nameof(GameMain.UnlockFullscreenPauseOneFrame))]
    public static bool UnlockFullscreenPauseOneFrame_Prefix()
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient)
            Multiplayer.Session.Network.SendPacket(new GamePauseStepRequest());
        else Multiplayer.Session.World.HostStepWorld();
        return false;
    }
}
