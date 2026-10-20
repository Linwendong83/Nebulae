
#region
using NebulaeModel.Networking;
using NebulaeWorld.Chat;
using UnityEngine;

#endregion

namespace NebulaeWorld;

internal static class Multiplayer
{
    internal sealed class ConnectionMemory
    {
        public string RecordId { get; set; }
        public string Address { get; set; }
        public string Password { get; set; }
        public string TransientWorldId { get; set; }
        public NebulaeModel.PersonalGoalProfile TransientGoals { get; set; }
    }

    public static ConnectionMemory LastConnection { get; private set; }
    public static MultiplayerSession Session { get; set; }

    public static bool IsActive => Session != null;

    public static bool IsLeavingGame { get; set; }
    public static bool ShouldReturnToJoinMenu { get; set; }

    public static bool IsInMultiplayerMenu { get; set; }

    public static bool IsDedicated { get; set; }
    public static bool ProtocolReady { get; set; }

    public static void HostGame(IServer server)
    {
        if (!ProtocolReady) throw new System.InvalidOperationException("Multiplayer protocol patches are unavailable");
        IsLeavingGame = false;

        Session = new MultiplayerSession(server);
        Session.Server!.Start();
    }

    public static void JoinGame(IClient client, string recordId = null, string address = null, string password = null)
    {
        if (!ProtocolReady) throw new System.InvalidOperationException("Multiplayer protocol patches are unavailable");
        IsLeavingGame = false;
        if (!string.IsNullOrWhiteSpace(address))
        {
            var previous = LastConnection;
            LastConnection = new ConnectionMemory
            {
                RecordId = recordId,
                Address = address,
                Password = password ?? "",
                TransientWorldId = previous?.Address == address && previous.RecordId == recordId
                    ? previous.TransientWorldId : null,
                TransientGoals = previous?.Address == address && previous.RecordId == recordId
                    ? previous.TransientGoals : null
            };
        }

        Session = new MultiplayerSession(client);
        Session.Client!.Start();
    }

    public static void LeaveGame()
    {
        IsLeavingGame = true;

        var wasGameLoaded = Session?.IsGameLoaded ?? false;

        Session?.Dispose();
        Session = null;
        if (wasGameLoaded) IsInMultiplayerMenu = false;

        if (wasGameLoaded)
        {
            if (!UIRoot.instance.backToMainMenu)
            {
                UIRoot.instance.backToMainMenu = true;
                DSPGame.EndGame();
            }
        }
        else if (ShouldReturnToJoinMenu)
        {
            UIRoot.instance.CloseMainMenuUI();
            var overlayCanvasGo = GameObject.Find("Overlay Canvas");
            var multiplayerMenu = overlayCanvasGo?.transform.Find("Nebulae - Multiplayer Menu");
            if (multiplayerMenu != null)
            {
                multiplayerMenu.SetAsLastSibling();
                multiplayerMenu.gameObject.SetActive(true);
            }
            else UIRoot.instance.OpenMainMenuUI();
        }
        ChatService.Instance.ClearMessages(_ => true);
    }
}
