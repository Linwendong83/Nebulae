using System;
using System.Collections;
using HarmonyLib;
using NebulaeModel;
using NebulaeModel.Logger;
using NebulaeModel.Networking;
using NebulaeNetwork;
using NebulaeWorld;
using NebulaeWorld.MonoBehaviours.Local;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIMainMenu))]
internal static class UIMainMenu_Patch
{
    private static RectTransform multiplayerButton;
    private static float rowPitch;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMainMenu._OnOpen))]
    public static void _OnOpen_Postfix(UIMainMenu __instance)
    {
        Multiplayer.IsLeavingGame = false;
        var group = GameObject.Find("Main Menu/button-group")?.GetComponent<RectTransform>();
        if (group == null) return;
        // A destroyed clone is != null in Unity, so check the overload as well
        if (multiplayerButton == null || !multiplayerButton)
        {
            var template = group.Find("button-new")?.GetComponent<RectTransform>();
            if (template == null) return;
            multiplayerButton = Object.Instantiate(template, group, false);
            multiplayerButton.name = "button-multiplayer";
            rowPitch = GetRowPitch(__instance, multiplayerButton);
            // Keep the sibling order in sync with the visual order (right below 载入存档)
            if (__instance?.loadGameBtnRt != null)
            {
                multiplayerButton.SetSiblingIndex(__instance.loadGameBtnRt.GetSiblingIndex() + 1);
            }
            NebulaeLocalizedText.Set(multiplayerButton.GetComponentInChildren<Text>(), "Multiplayer");
            var button = multiplayerButton.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(new UnityAction(OnMultiplayerButtonClick));
        }
        LayoutRow(__instance);
        MultiplayerPage.Create();
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMainMenu._OnUpdate))]
    public static void _OnUpdate_Postfix(UIMainMenu __instance)
    {
        LayoutRow(__instance);
    }

    /// <summary>
    /// Place the multiplayer button one row below 载入存档 and push the rows below it down by one row.
    /// UIMainMenu._OnUpdate rewrites the vanilla button positions every frame (and never touches
    /// the galaxy button), so the layout is re-applied from a reference row instead of accumulated.
    /// </summary>
    private static void LayoutRow(UIMainMenu __instance)
    {
        if (multiplayerButton == null || !multiplayerButton || __instance?.loadGameBtnRt == null) return;
        if (rowPitch <= 0f) rowPitch = GetRowPitch(__instance, multiplayerButton);
        if (rowPitch <= 0f) return;
        var referenceY = __instance.loadGameBtnRt.anchoredPosition.y;
        multiplayerButton.anchoredPosition = new Vector2(multiplayerButton.anchoredPosition.x,
            referenceY - rowPitch);
        // Vanilla re-assigns these rows from its cached positions on every _OnUpdate, so a constant
        // offset applied in the postfix moves them one row down in both menu layouts.
        Shift(__instance.optionsBtnRt, rowPitch);
        Shift(__instance.creditsBtnRt, rowPitch);
        Shift(__instance.exitBtnRt, rowPitch);
        // The galaxy button is only toggled by vanilla, never re-positioned, so set it absolutely
        // to keep it from drifting while it is hidden.
        SetY(__instance.galaxyButton != null ? __instance.galaxyButton.GetComponent<RectTransform>() : null,
            referenceY - rowPitch * 2f);
    }

    private static void SetY(RectTransform rect, float y)
    {
        if (rect == null) return;
        rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, y);
    }

    private static void Shift(RectTransform rect, float dy)
    {
        if (rect == null) return;
        var position = rect.anchoredPosition;
        rect.anchoredPosition = new Vector2(position.x, position.y - dy);
    }

    private static float GetRowPitch(UIMainMenu menu, RectTransform button)
    {
        // The vanilla rows are evenly spaced, so derive the pitch from them instead of hardcoding it
        if (menu?.newGameBtnRt != null && menu.continueBtnRt != null)
        {
            var pitch = Mathf.Abs(menu.newGameBtnRt.anchoredPosition.y - menu.continueBtnRt.anchoredPosition.y);
            if (pitch > 1f) return pitch;
        }
        return button != null ? button.sizeDelta.y + 10f : 0f;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIMainMenu.UpdateDemoScene))]
    [HarmonyPatch(nameof(UIMainMenu._OnUpdate))]
    public static void OnEscSwitch()
    {
        if (!VFInput.escape) return;
        if (UIRoot.instance.loadGameWindow.active && Multiplayer.IsInMultiplayerMenu)
        {
            UIRoot.instance.loadGameWindow.OnCancelClick(0);
            VFInput.UseEscape();
        }
        else if (MultiplayerPage.IsOpen)
        {
            MultiplayerPage.Close();
            VFInput.UseEscape();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMainMenu.OnUpdateLogButtonClick))]
    public static void OnUpdateLogButtonClick_Postfix()
    {
        if (MultiplayerPage.IsOpen) MultiplayerPage.Close();
    }

    public static void OnMultiplayerButtonClick()
    {
        if (Multiplayer.IsDedicated) { Multiplayer.IsInMultiplayerMenu = true; return; }
        MultiplayerPage.Open();
    }

    public static void JoinGame(string address, string password = "", string recordId = null)
    {
        if (!ServerAddress.TryParse(address, Config.Options.HostPort, out var parsed))
        {
            InGamePopup.ShowWarning("Invalid Address".Translate(), "Enter a valid server address".Translate(), "OK".Translate());
            return;
        }
        Multiplayer.ShouldReturnToJoinMenu = true;
        UIRoot.instance.StartCoroutine(TryConnect(parsed, address.Trim(), password ?? "", recordId));
    }

    private static IEnumerator TryConnect(ServerAddress parsed, string address, string password, string recordId)
    {
        if (MultiplayerPage.IsOpen) MultiplayerPage.HideForConnection();
        UIRoot.instance.OpenLoadingUI();
        // Let the native loading splash render before the synchronous socket connection starts.
        yield return null;
        try
        {
            var client = new Client(parsed.Host, parsed.Port, parsed.Protocol, password);
            client.AuthenticationFailed += () =>
            {
                Multiplayer.LeaveGame();
                MultiplayerPage.ShowPasswordPrompt(address, password, recordId);
            };
            Multiplayer.JoinGame(client, recordId, address, password);
        }
        catch (Exception e)
        {
            Log.Error("ConnectToServer error:\n" + e);
            UIRoot.instance.CloseLoadingUI();
            InGamePopup.ShowWarning("Connect failed".Translate(),
                "Was not able to connect to server".Translate(), "OK".Translate());
            MultiplayerPage.ShowAfterDisconnect();
        }
    }
}
