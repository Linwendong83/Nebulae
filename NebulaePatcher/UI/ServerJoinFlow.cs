using System;
using System.Collections;
using NebulaeModel;
using NebulaeModel.Logger;
using NebulaeModel.Networking;
using NebulaeNetwork;
using NebulaeWorld;
using UnityEngine;

namespace NebulaePatcher.UI;

/// <summary>
/// Starts a connection from the main-menu UI: validates the address, shows the loading splash and
/// hands the client to <see cref="Multiplayer"/>. Lives next to the page so the menu patch only
/// carries Harmony hooks.
/// </summary>
internal static class ServerJoinFlow
{
    public static void Join(string address, string password = "", string recordId = null)
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
