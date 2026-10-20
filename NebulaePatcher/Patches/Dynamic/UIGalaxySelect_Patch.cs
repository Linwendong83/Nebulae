using HarmonyLib;
using NebulaeModel.Packets.Session;
using NebulaeWorld;
using UnityEngine.UI;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIGalaxySelect))]
internal class UIGalaxySelect_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIGalaxySelect.OnSeedInputSubmit))]
    [HarmonyPatch(nameof(UIGalaxySelect.OnSeedInputValueChange))]
    [HarmonyPatch(nameof(UIGalaxySelect.OnStarCountSliderValueChange))]
    [HarmonyPatch(nameof(UIGalaxySelect.OnResourceMultiplierValueChange))]
    [HarmonyPatch(nameof(UIGalaxySelect.OnSandboxToggleValueChanged))]
    [HarmonyPatch(nameof(UIGalaxySelect.OnDarkFogToggleValueChanged))]
    [HarmonyPatch(nameof(UIGalaxySelect.OnStarNameLocalGenToggleValueChanged))]
    [HarmonyPatch(nameof(UIGalaxySelect.Rerand))]
    public static bool ClientSettings_Prefix() => !Multiplayer.IsActive || Multiplayer.Session.IsServer;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIGalaxySelect._OnOpen))]
    public static void OnOpen_Postfix(UIGalaxySelect __instance)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient) return;
        SetSettingsInteractable(__instance, false);
    }

    private static void SetSettingsInteractable(UIGalaxySelect instance, bool value)
    {
        instance.starNameLocalGenToggle.toggle.interactable = value;
        // Retain vanilla layout. The server owns generation settings.
        var group = instance.transform.Find("setting-group");
        foreach (var control in instance.GetComponentsInChildren<Selectable>(true))
        {
            if ((group != null && control.transform.IsChildOf(group)) || control.name == "random-button")
                control.interactable = value;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIGalaxySelect.ApplySetting))]
    public static bool ApplySetting_Prefix(UIGalaxySelect __instance)
    {
        if (!Multiplayer.IsInMultiplayerMenu) return true;
        var session = Multiplayer.Session;
        if (session is null || __instance.gameDesc == null) return false;
        if (session.IsServer) return true; // Includes vanilla goal selection.

        // The combat settings panel is not created on every multiplayer menu path.
        // Applying settings from those paths must still start the client handshake.
        if (__instance.uiCombat == null || !__instance.uiCombat.active)
            session.Network?.SendPacket(new StartGameMessage());
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIGalaxySelect.CancelSelect))]
    public static bool CancelSelect_Prefix(UIGalaxySelect __instance)
    {
        if (!Multiplayer.IsInMultiplayerMenu || !Multiplayer.IsActive || Multiplayer.Session?.IsInLobby != true)
            return true;
        __instance._Close();
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIGalaxySelect._OnClose))]
    public static void OnClose_Prefix(UIGalaxySelect __instance)
    {
        if (Multiplayer.IsInMultiplayerMenu && Multiplayer.IsActive && Multiplayer.Session.IsInLobby)
        {
            Multiplayer.ShouldReturnToJoinMenu = true;
            Multiplayer.Session.IsInLobby = false;
            Multiplayer.LeaveGame();
        }
        SetSettingsInteractable(__instance, true);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIGalaxySelect.UpdateParametersUIDisplay))]
    public static void UpdateParametersUIDisplay_Postfix(UIGalaxySelect __instance)
    {
        if (!Multiplayer.IsActive) return;
        var desc = __instance.gameDesc;
        if (Multiplayer.Session.IsClient)
        {
            // Updating the check mark must not run the vanilla callback, which uses the
            // client's language and would replace the server's naming setting.
            __instance.starNameLocalGenToggle.toggle.SetIsOnWithoutNotify(desc.starNameLCID == 2052);
            __instance.starNameGroupRect.gameObject.SetActive(Localization.isZHCN || desc.starNameLCID == 2052);
            return;
        }
        if (!Multiplayer.IsInMultiplayerMenu || !Multiplayer.Session.IsServer) return;
        var packet = new LobbyUpdateValues(desc);
        var server = Multiplayer.Session.Server;
        server.SendToPlayers(server.Players.Syncing, packet);
        server.SendToPlayers(server.Players.Pending, packet);
    }
}

[HarmonyPatch(typeof(UIGoalSetting), nameof(UIGoalSetting.OnGoalButtonClick))]
internal class LobbyGoalSetting_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(UIGoalSetting __instance, int _data)
    {
        if (!Multiplayer.IsActive || !__instance.galaxySelect.active) return true;
        if (Multiplayer.Session.IsClient) return false;
        if (_data < (int)EGoalLevel.Off || _data > (int)EGoalLevel.Full) return false;
        NebulaeWorld.GameStates.GoalManager.PendingHostLevel = (EGoalLevel)_data;
        __instance.gameDesc.goalLevel = (EGoalLevel)_data;
        UIGalaxySelect_Patch.UpdateParametersUIDisplay_Postfix(__instance.galaxySelect);
        Multiplayer.Session.IsInLobby = false;
        DSPGame.StartGameSkipPrologue(__instance.gameDesc);
        return false;
    }
}
