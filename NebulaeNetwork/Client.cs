using NebulaeModel.Packets;
#region

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using HarmonyLib;
using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel;
using NebulaeModel.Logger;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.GameStates;
using NebulaeModel.Packets.Routers;
using NebulaeModel.Packets.Session;
using NebulaeModel.Utils;
using NebulaeWorld;
using NebulaeWorld.GameStates;
using UnityEngine;
using WebSocketSharp;

#endregion

namespace NebulaeNetwork;

internal class Client : IClient
{
    public INetPacketProcessor PacketProcessor { get; set; } = new NebulaeNetPacketProcessor();

    private const float FRAGEMENT_UPDATE_INTERVAL = 0.1f;
    private const float GAME_STATE_UPDATE_INTERVAL = 1f;
    private const float JOIN_RESPONSE_TIMEOUT = 25f;

    private readonly AccessTools.FieldRef<WebSocket, MemoryStream> fragmentsBufferRef =
        AccessTools.FieldRefAccess<WebSocket, MemoryStream>("_fragmentsBuffer");

    private readonly string serverPassword;
    private readonly string serverProtocol;

    private WebSocket clientSocket;

    private float fragmentUpdateTimer;
    private float gameStateUpdateTimer;
    private float joinResponseTimer;
    private NebulaeConnection serverConnection;
    private bool websocketAuthenticationFailure;

    public event Action AuthenticationFailed;

    /// <summary>
    ///     Connect to a server by host name or IP literal. The host is kept as configured: it is
    ///     resolved by the websocket layer when the connection is opened, not here.
    /// </summary>
    public Client(string host, int port, string protocol, string password = "")
    {
        ServerHost = NetUtils.NormalizeHost(host);
        ServerPort = port;
        serverProtocol = string.IsNullOrWhiteSpace(protocol) ? "ws" : protocol;
        serverPassword = password;
    }

    public Client(IPEndPoint endpoint, string protocol = "", string password = "")
        : this(endpoint.Address.ToString(), endpoint.Port, protocol, password)
    {
    }

    /// <summary>
    ///     Host name or IP literal as configured, without scheme, port or IPv6 brackets.
    /// </summary>
    public string ServerHost { get; }

    /// <summary>
    ///     Port of the server.
    /// </summary>
    public int ServerPort { get; }

    /// <summary>
    ///     Websocket scheme used to connect: "ws" or "wss".
    /// </summary>
    public string ServerProtocol => serverProtocol;

    /// <summary>
    ///     Websocket url built from the configured host, without resolving it.
    /// </summary>
    public string SocketUrl => NetUtils.MakeWebSocketUrl(serverProtocol, ServerHost, ServerPort);

    public void Start()
    {
        RuntimePacketRegistry.Register((NebulaeNetPacketProcessor)PacketProcessor, false);

        clientSocket = new WebSocket(SocketUrl);
        clientSocket.Log.Level = LogLevel.Warn;
        clientSocket.Log.Output = Log.SocketOutput;
        clientSocket.OnOpen += ClientSocket_OnOpen;
        clientSocket.OnClose += ClientSocket_OnClose;
        clientSocket.OnMessage += ClientSocket_OnMessage;

        var currentLogOutput = clientSocket.Log.Output;
        clientSocket.Log.Output = (logData, arg2) =>
        {
            currentLogOutput(logData, arg2);

            // This method of detecting an authentication failure is super finicky, however there is no other way to do this in the websocket package we are currently using
            if (logData.Level == LogLevel.Fatal && logData.Message == "Requires the authentication.")
            {
                websocketAuthenticationFailure = true;
            }
        };

        if (!string.IsNullOrWhiteSpace(serverPassword))
        {
            clientSocket.SetCredentials("nebulae-player", serverPassword, true);
        }

        websocketAuthenticationFailure = false;

        clientSocket.Connect();

        ((LocalPlayer)Multiplayer.Session.LocalPlayer).IsHost = false;

    }

    public void Stop()
    {
        if (clientSocket?.IsAlive == true && Multiplayer.Session?.IsGameLoaded == true &&
            Multiplayer.Session.JoinSync.CanInteract && GameMain.mainPlayer != null)
        {
            Multiplayer.Session.Life.Publish();
            var exitingConnection = serverConnection;
            if (exitingConnection != null && !exitingConnection.FlushSendQueue())
                Diagnostics.Warn("client:exit-flush", () => $"[network] final checkpoint send did not flush connection={exitingConnection.Id} {exitingConnection.SendDiagnostics}");
        }
        clientSocket?.Close((ushort)DisconnectionReason.ClientRequestedDisconnect, "Player left the game");
        serverConnection?.StopSending();

    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    public void SendPacket<T>(T packet) where T : class, new()
    {
        StampVegetationOperation(packet);
        serverConnection?.SendPacket(packet);
    }

    public void SendToMatching<T>(T packet, Predicate<INebulaePlayer> condition) where T : class, new()
    {
        throw new NotImplementedException();
    }

    public void SendPacketExclude<T>(T packet, INebulaeConnection exclude) where T : class, new()
    {
        // Only possible from host
        throw new NotImplementedException();
    }

    public byte[] WriteOnce<T>(T packet) where T : class, new()
    {
        StampVegetationOperation(packet);
        lock (PacketProcessor)
        {
            return PacketProcessor.Write(packet);
        }
    }

    private static void StampVegetationOperation(object packet)
    {
        if (packet is IVegetationOperation operation && operation.CommandSequence == 0)
            operation.CommandSequence = Multiplayer.Session?.Vegetation?.NextCommandSequence() ?? 0;
    }

    public void SendPacketToLocalStar<T>(T packet) where T : class, new()
    {
        serverConnection?.SendPacket(new StarBroadcastPacket(WriteOnce(packet), GameMain.data.localStar?.id ?? -1));
    }

    public void SendPacketToLocalPlanet<T>(T packet) where T : class, new()
    {
        serverConnection?.SendPacket(new PlanetBroadcastPacket(WriteOnce(packet), GameMain.data.localPlanet?.id ?? -1));
    }

    public void SendPacketToPlanet<T>(T packet, int planetId) where T : class, new()
    {
        // Only possible from host
        throw new NotImplementedException();
    }

    public void SendPacketToStar<T>(T packet, int starId) where T : class, new()
    {
        // Only possible from host
        throw new NotImplementedException();
    }

    public void SendPacketToStarExclude<T>(T packet, int starId, INebulaeConnection exclude) where T : class, new()
    {
        // Only possible from host
        throw new NotImplementedException();
    }

    public void Update()
    {
        // Loading the local baseline is only the first stage of joining. Keep the larger
        // budget until replay and authority baselines are ready, rather than throttling
        // the remaining synchronization as soon as GameLoader finishes.
        PacketProcessor.ProcessPacketQueue(PacketBudget(Time.unscaledDeltaTime,
            !Multiplayer.Session.IsGameLoaded || Multiplayer.Session.JoinSync.LocalPending));
        Multiplayer.Session.Planets.Update();
        Multiplayer.Session.JoinSync.UpdateClient();
        Multiplayer.Session.InventoryTransfers.CheckDiagnostics();
        serverConnection?.CheckSendDiagnostics();

        if (Multiplayer.Session.IsGameLoaded && Multiplayer.Session.WorldPaused && !Multiplayer.Session.JoinSync.LocalPending)
        {
            // A stopped clock still has inventory receipts and replica messages to settle.
            NebulaeWorld.GameStates.JoinSyncManager.WaitForSimulation();
            Multiplayer.Session.AuthorityRuntime?.OnFrameBoundary(GameMain.gameTick);
            Multiplayer.Session.Life.GameTick();
        }

        WatchdogJoinResponse();

        if (Multiplayer.Session.IsGameLoaded)
        {
            Multiplayer.Session.State.ServiceClock();
            Multiplayer.Session.PowerTowers.SendLocalStateIfChanged();

            gameStateUpdateTimer += Time.unscaledDeltaTime;
            if (gameStateUpdateTimer >= GAME_STATE_UPDATE_INTERVAL)
            {
                if (!GameMain.isFullscreenPaused)
                {
                    SendPacket(new GameStateRequest());
                }

                gameStateUpdateTimer = 0f;
            }
        }

        fragmentUpdateTimer += Time.unscaledDeltaTime;
        if (!(fragmentUpdateTimer >= FRAGEMENT_UPDATE_INTERVAL))
        {
            return;
        }

        if (GameStatesManager.FragmentSize > 0)
        {
            var received = GetFragmentBufferLength();
            Multiplayer.Session.JoinSync.TransferProgress(received);
        }

        fragmentUpdateTimer = 0f;
    }

    // Keep approximately the same network service per wall second when rendering slows.
    // A fixed two milliseconds per rendered frame otherwise starves lifecycle and clock packets
    // precisely when old combat objects most need the host's removal messages.
    internal static double PacketBudget(double elapsedSeconds, bool joining)
    {
        if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds < 0) elapsedSeconds = 0;
        return Math.Max(joining ? 8 : 2, Math.Min(50, elapsedSeconds * 120));
    }

    /// <summary>Shows a visible error when the server accepts the socket but does not answer the lobby
    /// request.</summary>
    private void WatchdogJoinResponse()
    {
        var session = Multiplayer.Session;
        if (session == null || session.IsInLobby || session.IsGameLoaded || session.LocalPlayer.IsInitialDataReceived)
        {
            joinResponseTimer = 0f;
            return;
        }

        joinResponseTimer += Time.unscaledDeltaTime;
        if (joinResponseTimer <= JOIN_RESPONSE_TIMEOUT) return;

        joinResponseTimer = 0f;
        Log.Warn("Server did not answer the join request in time");
        UIRoot.instance?.CloseLoadingUI();
        InGamePopup.ShowWarning(
            "Server Unavailable".Translate(),
            "Server did not respond to join request.\nPlease make sure the server is running and uses the same mod version.".Translate(),
            "OK".Translate(),
            Multiplayer.LeaveGame);
    }

    private void ClientSocket_OnMessage(object sender, MessageEventArgs e)
    {
        var connection = serverConnection;
        if (Multiplayer.IsLeavingGame || connection == null)
        {
            return;
        }

        byte[] packet;
        try
        {
            if (!e.IsBinary) throw new InvalidDataException("Packet frames require binary websocket messages");
            packet = connection.ReceivePacket(PacketCompression.UnwrapForReceive(e.RawData));
        }
        catch (InvalidDataException exception)
        {
            connection.StopSending();
            // A message the framing cannot read means the stream itself is corrupt: staying on it
            // would only feed the processors garbage, so close and let the close handler surface it.
            Diagnostics.Warn("client:unreadable:" + connection.Id, () => $"[network] unreadable message connection={connection.Id} status={connection.ConnectionStatus} bytes={e.RawData?.Length} binary={e.IsBinary}", exception);
            UnityDispatchQueue.RunOnMainThread(() =>
                clientSocket?.Close((ushort)DisconnectionReason.InvalidData, "Corrupt packet stream"));
            return;
        }

        if (packet != null) PacketProcessor.EnqueuePacketForProcessing(packet, connection);
    }

    private void ClientSocket_OnOpen(object sender, EventArgs e)
    {
        DisableNagleAlgorithm(clientSocket);

        Log.Info("Server connection established");
        serverConnection = new NebulaeConnection(clientSocket, new DnsEndPoint(ServerHost, ServerPort),
            PacketProcessor as NebulaeNetPacketProcessor);

        var connection = Multiplayer.LastConnection;
        if (connection?.RecordId != null && !string.IsNullOrEmpty(serverPassword))
        {
            try
            {
                ServerMemoryStore.Instance.UpdatePassword(connection.RecordId, serverPassword);
            }
            catch (Exception exception)
            {
                Log.Warn($"Could not save password for server record: {exception.Message}");
            }
        }


        SendPacket(new LobbyRequest(
            CryptoUtils.GetPublicKey(CryptoUtils.GetOrCreateUserCert()),
            GameMain.data.account.userName));
    }

    private void ClientSocket_OnClose(object sender, CloseEventArgs e)
    {
        var closedConnection = serverConnection;
        if (e.Code == (ushort)DisconnectionReason.ClientRequestedDisconnect || e.Code == 1000)
            Log.Debug($"[network] client close connection={closedConnection?.Id} code={e.Code} clean={e.WasClean} reason={e.Reason}");
        else Diagnostics.Warn("client:close:" + closedConnection?.Id,
            () => $"[network] client connection lost connection={closedConnection?.Id} status={closedConnection?.ConnectionStatus} loaded={Multiplayer.Session?.IsGameLoaded} joining={Multiplayer.Session?.JoinSync.LocalPending} code={e.Code} clean={e.WasClean} reason={e.Reason}");
        if (serverConnection != null)
        {
            serverConnection.StopSending();
            serverConnection = null;
        }

        UnityDispatchQueue.RunOnMainThread(() =>
        {
            // A close queued by the previous client can run after another session starts.
            if (!ReferenceEquals(Multiplayer.Session?.Client, this)) return;
            HandleSocketClosed(e);
        });
    }

    private void HandleSocketClosed(CloseEventArgs e)
    {
        Multiplayer.Session?.JoinSync.CancelLocal();
        if (Multiplayer.Session?.IsGameLoaded != true) UIRoot.instance?.CloseLoadingUI();
        // If the client is Quitting by himself, we don't have to inform him of his disconnection.
        if (e.Code == (ushort)DisconnectionReason.ClientRequestedDisconnect)
        {
            return;
        }

        // Opens the pause menu on disconnection to prevent NRE when leaving the game
        if (Multiplayer.Session?.IsGameLoaded ?? false)
        {
            GameMain.instance._paused = true;
        }

        switch (e.Code)
        {
            case (ushort)DisconnectionReason.ModVersionMismatch:
                {
                    var versions = e.Reason.Split(';');
                    if (versions.Length >= 3 && versions[0] == Config.ModInfo?.Metadata.GUID)
                    {
                        versions[1] = NebulaeVersion.Normalize(versions[1]);
                        versions[2] = NebulaeVersion.Normalize(versions[2]);
                    }
                    InGamePopup.ShowWarning(
                        "Mod Version Mismatch".Translate(),
                        string.Format("Your mod {0} version is not the same as the Host version.\nYou:{1} - Remote:{2}".Translate(),
                            versions[0], versions.Length > 1 ? versions[1] : "unknown", versions.Length > 2 ? versions[2] : "unknown"),
                        "OK".Translate(),
                        Multiplayer.LeaveGame);
                    return;
                }
            case (ushort)DisconnectionReason.ProtocolError when websocketAuthenticationFailure:
                if (AuthenticationFailed != null)
                {
                    AuthenticationFailed.Invoke();
                    return;
                }
                // Clients without an interactive retry handler still report the rejection.
                InGamePopup.ShowWarning(
                    (string.IsNullOrEmpty(serverPassword) ? "Password Required" : "Wrong Password").Translate(),
                    "Server is protected. Please enter the correct password:".Translate(),
                    "OK".Translate(),
                    Multiplayer.LeaveGame);
                return;
            case (ushort)DisconnectionReason.HostStillLoading:
                InGamePopup.ShowWarning(
                    "Server Busy".Translate(),
                    "Server is not ready to join. Please try again later.".Translate(),
                    "OK".Translate(),
                    Multiplayer.LeaveGame);
                return;
        }

        if (Multiplayer.Session != null && (Multiplayer.Session.IsGameLoaded || Multiplayer.Session.IsInLobby))
        {
            InGamePopup.ShowWarning(
                "Connection Lost".Translate(),
                "You have been disconnected from the server.".Translate() + "\n" + e.Reason.Translate(),
                "Quit",
                Multiplayer.LeaveGame);
            if (!Multiplayer.Session.IsInLobby)
            {
                return;
            }

            Multiplayer.ShouldReturnToJoinMenu = false;
            Multiplayer.Session.IsInLobby = false;
            UIRoot.instance.galaxySelect.CancelSelect();
        }
        else
        {
            Log.Warn("Disconnect code: " + e.Code + ", reason:" + e.Reason);
            InGamePopup.ShowWarning(
                "Server Unavailable".Translate(),
                "Can't reach the server. Please check the network status.\n(Refer Nebula wiki for troubleshooting)".Translate(),
                "OK".Translate(),
                Multiplayer.LeaveGame);
        }
    }

    private static void DisableNagleAlgorithm(WebSocket socket)
    {
        var tcpClient = AccessTools.FieldRefAccess<WebSocket, TcpClient>("_tcpClient")(socket);
        if (tcpClient != null)
        {
            tcpClient.NoDelay = true;
        }
    }

    private int GetFragmentBufferLength()
    {
        var fragmentsBuffer = fragmentsBufferRef(clientSocket);
        return (int)(fragmentsBuffer?.Length ?? 0);
    }
}
