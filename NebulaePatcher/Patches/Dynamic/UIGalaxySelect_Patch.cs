using System;
using HarmonyLib;
using NebulaeModel.Packets.Session;
using NebulaeWorld;
using UnityEngine.UI;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIGalaxySelect))]
internal class UIGalaxySelect_Patch
{
    public static bool UsesExtendedStarRange() => Multiplayer.IsInMultiplayerMenu || Multiplayer.IsActive;

    [HarmonyPrefix, HarmonyPatch(nameof(UIGalaxySelect._OnOpen))]
    public static void StarRange_Prefix(UIGalaxySelect __instance) => StarCountControl.Open(__instance);

    [HarmonyPostfix, HarmonyPatch(nameof(UIGalaxySelect._OnClose))]
    public static void RestoreStarRange(UIGalaxySelect __instance) => StarCountControl.Close(__instance);

    public static void SetStarCountDisplay(Slider slider, float value)
    {
        if (UsesExtendedStarRange()) slider.SetValueWithoutNotify(Math.Min(value, StarCountSelection.More));
        else slider.value = value;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(UIGalaxySelect.OnStarCountSliderValueChange))]
    public static bool StarCount_Prefix(UIGalaxySelect __instance)
    {
        if (!UsesExtendedStarRange()) return true;
        StarCountControl.ChangeSlider(__instance);
        return false;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(UIGalaxySelect.UpdateUIDisplay))]
    public static void StarCountDisplay_Postfix(UIGalaxySelect __instance) => StarCountControl.Refresh(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIGalaxySelect.OnSeedInputSubmit))]
    [HarmonyPatch(nameof(UIGalaxySelect.OnSeedInputValueChange))]
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
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient) SetSettingsInteractable(__instance, false);
        StarCountControl.Refresh(__instance);
    }

    private static void SetSettingsInteractable(UIGalaxySelect instance, bool value)
    {
        instance.starNameLocalGenToggle.toggle.interactable = value;
        // Retain vanilla layout. The server owns generation settings.
        var group = instance.transform.Find("setting-group");
        foreach (var control in instance.GetComponentsInChildren<Selectable>(true))
        {
            // The custom field's enabled state is owned by its slider selection.
            if (control.name == StarCountControl.InputName) continue;
            if ((group != null && control.transform.IsChildOf(group)) || control.name == "random-button")
                control.interactable = value;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIGalaxySelect.ApplySetting))]
    public static bool ApplySetting_Prefix(UIGalaxySelect __instance)
    {
        if (!Multiplayer.IsInMultiplayerMenu) return true;
        StarCountControl.SubmitPending(__instance);
        var session = Multiplayer.Session;
        if (session is null || NebulaeModel.Utils.NativeGameAccess.GetGalaxyDescription(__instance) == null) return false;
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
        // Closing can clear the session; stop input events before that happens.
        RestoreStarRange(__instance);
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
        var desc = NebulaeModel.Utils.NativeGameAccess.GetGalaxyDescription(__instance);
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
        foreach (var player in server.Players.Pending.Values)
            if ((player.Data as NebulaeModel.DataStructures.PlayerData)?.SessionCounted == true)
                player.SendPacket(packet);
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
