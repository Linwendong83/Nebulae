
#region
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using HarmonyLib;
using NebulaePatcher.Patches.Transpilers;
using NebulaeWorld;
using UnityEngine;
using UnityEngine.UI;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIEscMenu))]
internal class UIEscMenu_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIEscMenu._OnOpen))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnOpen_Prefix(UIEscMenu __instance)
    {
        // Disable save game button if you are a client in a multiplayer session
        var saveGameWindowButton = __instance.button2;
        SetButtonEnableState(saveGameWindowButton, !Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost);

        // Disable load game button if in a multiplayer session
        var loadGameWindowButton = __instance.button3;
        SetButtonEnableState(loadGameWindowButton, !Multiplayer.IsActive);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIEscMenu.OnButton2Click))]
    public static bool OnButton2Click_Prefix()
    {
        return !Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost;
    }


    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIEscMenu.OnButton5Click))]
    public static bool OnButton5Click_Prefix(UIEscMenu __instance)
    {
        if (DeferClientExit(() => __instance.OnButton5Click())) return false;
        QuitGame();
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIEscMenu.OnButton6Click))]
    public static bool OnButton6Click_Prefix(UIEscMenu __instance)
    {
        if (DeferClientExit(() => __instance.OnButton6Click())) return false;
        QuitGame();
        return true;
    }

    private static bool DeferClientExit(System.Action complete)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient || GameMain.mainPlayer?.mecha == null) return false;
        if (!Multiplayer.Session.Life.IsExiting)
        {
            UIRealtimeTip.Popup("Saving personal progress...".Translate());
            NebulaeWorld.GameStates.LocalPlayerPreferences.SaveDashboard();
            Multiplayer.Session.Life.BeginExit(() => { QuitGame(); complete(); });
        }
        return true;
    }

    private static void QuitGame()
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        if (Multiplayer.Session.LocalPlayer.IsHost)
        {
            // Because GameSave.SaveAsLastExit() is disable, we have to save game here to match the vanilla behavior.
            GameSave.SaveCurrentGame(GameSave.LastExit);
        }
        else if (GameMain.mainPlayer?.mecha != null)
        {
            NebulaeWorld.GameStates.LocalPlayerPreferences.SaveDashboard();
        }
        PlanetFactory_Transpiler.CheckPopupPresent.Clear();
        PlanetFactory_Transpiler.FaultyVeins.Clear();
        Multiplayer.LeaveGame();
    }

    private static void SetButtonEnableState(Selectable button, bool enable)
    {
        var buttonColors = button.colors;
        buttonColors.disabledColor = new Color(1f, 1f, 1f, 0.15f);
        button.interactable = enable;
        button.colors = buttonColors;
    }
}
