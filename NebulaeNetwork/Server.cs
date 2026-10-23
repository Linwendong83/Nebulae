using NebulaeModel.Packets;
#region

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel;
using NebulaeModel.Logger;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.GameHistory;
using NebulaeModel.Packets.Session;
using NebulaeModel.Utils;
using NebulaeNetwork.Messaging;
using NebulaeWorld;
using NebulaeWorld.Statistics;
using Open.Nat;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;
using AuthenticationSchemes = WebSocketSharp.Net.AuthenticationSchemes;
using NetworkCredential = WebSocketSharp.Net.NetworkCredential;

#endregion

namespace NebulaeNetwork;

internal class Server : IServer
{
    public INetPacketProcessor PacketProcessor { get; set; } = new NebulaeNetPacketProcessor();

    private const float GAME_RESEARCH_UPDATE_INTERVAL = 2;
    private const float LAUNCH_UPDATE_INTERVAL = 4;
    private const float DYSONSPHERE_UPDATE_INTERVAL = 2;
    private const float WARNING_UPDATE_INTERVAL = 1;
    private readonly bool loadSaveFile;

    private float dysonLaunchUpateTimer = 1;
    private float dysonSphereUpdateTimer;

    private float gameResearchHashUpdateTimer;

    private WebSocketServer socket;
    private float warningUpdateTimer;

    public ConcurrentPlayerCollection Players { get; } = new();

    private ConcurrentQueue<ushort> PlayerIdPool = new();
    private int highestPlayerID;

    public Server(ushort port, bool loadSaveFile = false)
    {
        Port = port;
        this.loadSaveFile = loadSaveFile;
    }

    public ushort Port { get; set; }

    internal void OnSocketConnection(INebulaeConnection conn)
    {
        // Generate new data for the player
        var playerId = GetNextPlayerId();

        // this is truncated to ushort.MaxValue
        // A lobby can exist before the actual galaxy does. Assign birth on StartGame, on the server.
        var playerData = new PlayerData(playerId, -1);

        conn.ConnectionStatus = EConnectionStatus.Pending;

        INebulaePlayer newPlayer = new NebulaePlayer(conn, playerData);
        if (!Players.TryAdd(conn, newPlayer))
            throw new InvalidOperationException($"Connection {conn.Id} already exists!");
    }

    private ushort GetNextPlayerId()
    {
        ushort nextId;
        if (!PlayerIdPool.TryDequeue(out nextId))
            nextId = (ushort)Interlocked.Increment(ref highestPlayerID);
        return nextId;
    }

    internal void OnSocketDisconnection(INebulaeConnection conn)
    {
        (conn as NebulaeConnection)?.StopSending();
        if (Multiplayer.Session == null || Multiplayer.IsLeavingGame) return;
        Multiplayer.Session.JoinSync?.Forget(conn);
        Multiplayer.Session.Planets.HostPreparation.Forget(conn);

        var departing = Players.Get(conn, EConnectionStatus.Connected);
        if (departing != null)
        {
            Multiplayer.Session.Vegetation?.CaptureRemote(departing.Id);
            Multiplayer.Session.Vegetation?.ForgetRemote(departing.Id);
        }
        Players.TryRemove(conn, out var player);
        if (player == null) return; // Error and close callbacks can retire the same socket.

        // A connection that never completed the handshake (server list probe or a mid-handshake
        // drop) was never part of the session: recycle the id and skip the leave bookkeeping,
        // otherwise every browser refresh would broadcast a phantom PlayerDisconnected.
        if (player.Data is PlayerData { SessionCounted: false } && conn.ConnectionStatus == EConnectionStatus.Pending)
        {
            PlayerIdPool.Enqueue(player.Id);
            return;
        }

        Multiplayer.Session.BuildDispatch?.PlayerLeft(player.Id);
        // The departing player's connection epoch is retired, so a reconnect gets a fresh dedup
        // window and no queued command from the old connection can still be executed.
        Multiplayer.Session.AuthorityRuntime?.ForgetPlayerConnection(player.Id);
        Multiplayer.Session.Enemies?.QueueHivePreview(player.Id, 0);
        if (player.Data is PlayerData { SessionCounted: true } counted)
        {
            counted.SessionCounted = false;
            // The floor is the host's own seat; a headless server has none and may reach zero.
            var floor = Multiplayer.IsDedicated ? (ushort)0 : (ushort)1;
            Multiplayer.Session.NumPlayers = (ushort)Math.Max(floor, Multiplayer.Session.NumPlayers - 1);
        }

        // player is valid
        Multiplayer.Session.Metadata.Leave(player.Id);
        Multiplayer.Session.Life.Remove(player.Id);
        Multiplayer.Session.Goals.RemovePlayer(player.Id);
        Multiplayer.Session.Kills.Unsubscribe(player.Id);
        SendPacketExclude(new PlayerDisconnected(player.Id, Multiplayer.Session.NumPlayers), conn);
        // For sync completed player who triggered OnPlayerJoinedGame() before
        if (conn.ConnectionStatus == EConnectionStatus.Connected)
        {
            SimulatedWorld.OnPlayerLeftGame(player);
        }

        PlayerIdPool.Enqueue(player.Id);

        Multiplayer.Session.PowerTowers.RemovePlayer(player.Id);
        Multiplayer.Session.Statistics.UnRegisterPlayer(player.Id);
        Multiplayer.Session.FacilityDirectory.Remove(conn);
        Multiplayer.Session.DysonSpheres.UnRegisterPlayer(conn);

        Multiplayer.Session.World.IsPlayerJoining = Players.Syncing.Count != 0;
    }

    public void Start()
    {
        SaveManager.LoadServerData(loadSaveFile);

        RuntimePacketRegistry.Register((NebulaeNetPacketProcessor)PacketProcessor, true);

        if (Config.Options.EnableUPnpOrPmpSupport)
        {
            Task.Run(async () =>
            {
                var discoverer = new NatDiscoverer();
                try
                {
                    var device = await discoverer.DiscoverDeviceAsync();
                    await device.CreatePortMapAsync(new Mapping(Protocol.Tcp, Port, Port, "DSP nebulae"));
                    Log.Info($"Successfully created UPnp or Pmp port mapping for {Port}");
                }
                catch (NatDeviceNotFoundException)
                {
                    Log.Warn("No UPnp or Pmp compatible/enabled NAT device found".Translate());
                }
                catch (MappingException)
                {
                    Log.Warn("Could not create UPnp or Pmp port mapping".Translate());
                }
            });
        }

        socket = new WebSocketServer(IPAddress.IPv6Any, Port)
        {
            Log = { Level = LogLevel.Warn, Output = Log.SocketOutput }
        };

        if (!string.IsNullOrWhiteSpace(Config.Options.ServerPassword))
        {
            socket.AuthenticationSchemes = AuthenticationSchemes.Basic;
            socket.UserCredentialsFinder = id =>
            {
                var name = id.Name;

                // Return user name, password, and roles.
                return name == "nebulae-player"
                    ? new NetworkCredential(name, Config.Options.ServerPassword)
                    : null; // If the user credentials are not found.
            };
        }

        DisableNagleAlgorithm(socket);
        WebSocketService.PacketProcessor = PacketProcessor as NebulaeNetPacketProcessor;
        WebSocketService.Server = this;
        socket.AddWebSocketService<WebSocketService>("/socket", wse => new WebSocketService());
        try
        {
            // Set wait time higher for high latency network
            socket.WaitTime = TimeSpan.FromSeconds(20);
            socket.KeepClean = Config.Options.CleanupInactiveSessions;
            socket.Start();
        }
        catch (InvalidOperationException e)
        {
            Log.Warn(e.ToString());
            InGamePopup.ShowError("Error".Translate(), "An error occurred while hosting the game: ".Translate() + e.Message,
                "Close".Translate());
            Stop();
            Multiplayer.LeaveGame();
            return;
        }

        ((LocalPlayer)Multiplayer.Session.LocalPlayer).IsHost = true;

        ((LocalPlayer)Multiplayer.Session.LocalPlayer).SetPlayerData(new PlayerData(
                GetNextPlayerId(),
                GameMain.localPlanet?.id ?? -1,
                GameMain.data?.account.userName ?? string.Empty),
            loadSaveFile);

    }

    public void Stop()
    {
        socket?.Stop();

    }

    public void Disconnect(INebulaeConnection conn, DisconnectionReason reason, string reasonMessage = "")
    {
        OnSocketDisconnection(conn);
        if (Encoding.UTF8.GetBytes(reasonMessage).Length <= 123)
        {
            ((NebulaeConnection)conn).peerSocket.Close((ushort)reason, reasonMessage);
        }
        else
        {
            throw new ArgumentException("Reason string cannot take up more than 123 bytes");
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    // Just to make a single entry point for all sends.
    public void SendToPlayers<T>(IEnumerable<KeyValuePair<INebulaeConnection, INebulaePlayer>> players, T packet) where T : class, new()
    {
        // Serialized on the first recipient rather than up front, so a send that matches nobody costs
        // nothing, and one that matches everybody costs a single serializer pass instead of one per
        // recipient. The bytes are read-only from here on: framing copies them per connection.
        byte[] rawData = null;
        foreach (var kvp in players)
        {
            rawData ??= WriteOnce(packet);
            kvp.Key.SendRawPacket(rawData);
        }
    }

    public byte[] WriteOnce<T>(T packet) where T : class, new()
    {
        lock (PacketProcessor)
        {
            return PacketProcessor.Write(packet);
        }
    }

    public void SendPacket<T>(T packet) where T : class, new()
    {
        SendToMatching(packet, _ => true);
    }

    /// <summary>
    /// Send a packet to all players that match a predicate
    /// </summary>
    /// <param name="packet"></param>
    /// <param name="condition"></param>
    /// <typeparam name="T"></typeparam>
    public void SendToMatching<T>(T packet, Predicate<INebulaePlayer> condition)
        where T : class, new()
    {
        // Enumerated directly instead of through LINQ: every broadcast goes through here, and a
        // Where iterator per send is allocation the fan-out does not need.
        byte[] rawData = null;
        foreach (var kvp in Players.Connected)
        {
            if (!condition(kvp.Value))
            {
                continue;
            }
            rawData ??= WriteOnce(packet);
            kvp.Key.SendRawPacket(rawData);
        }
        if (Multiplayer.Session?.JoinSync?.HasJoiningPeers != true) return;
        foreach (var kvp in Players.Syncing)
        {
            if (!condition(kvp.Value)) continue;
            rawData ??= WriteOnce(packet);
            Multiplayer.Session.JoinSync.Capture(kvp.Key, packet, rawData);
        }
    }

    public void SendPacketToStar<T>(T packet, int starId) where T : class, new()
    {
        SendToMatching(packet, p => p.Data.LocalStarId == starId);
    }

    public void SendPacketToLocalStar<T>(T packet) where T : class, new()
    {
        var starId = GameMain.data.localStar?.id ?? -1;
        SendPacketToStar(packet, starId);
    }

    public void SendPacketToPlanet<T>(T packet, int planetId) where T : class, new()
    {
        SendToMatching(packet, p => p.Data.LocalPlanetId == planetId);
    }

    public void SendPacketToLocalPlanet<T>(T packet) where T : class, new()
    {
        var planetId = GameMain.data.localPlanet?.id ?? -1;
        SendPacketToPlanet(packet, planetId);
    }


    public void SendPacketExclude<T>(T packet, INebulaeConnection exclude) where T : class, new()
    {
        SendToMatching(packet, p => !p.Connection.Equals(exclude));
    }

    public void SendPacketToStarExclude<T>(T packet, int starId, INebulaeConnection exclude) where T : class, new()
    {
        SendToMatching(packet, p => p.Data.LocalStarId == starId && !p.Connection.Equals(exclude));
    }

    public void Update()
    {
        // Same per-frame budget the client uses: an unbounded drain can spend an entire
        // frame dequeuing a packet burst, and anything left over simply waits one frame.
        PacketProcessor.ProcessPacketQueue(Multiplayer.Session.IsGameLoaded ? 2 : 8);
        Multiplayer.Session.Planets.Update();
        Multiplayer.Session.JoinSync.UpdateServer();
        if (Multiplayer.Session.IsGameLoaded && (Multiplayer.Session.WorldPaused || GameMain.isPaused))
        {
            NebulaeWorld.GameStates.JoinSyncManager.WaitForSimulation();
            Multiplayer.Session.Ships.FlushLifecycle();
            Multiplayer.Session.FactoryProducts.TickHostFighterPorts(GameMain.gameTick, true);
            Multiplayer.Session.CombatAuthority.PumpPausedCheckpoints();
            Multiplayer.Session.CombatAuthority.FlushFighterPorts();
            Multiplayer.Session.AuthorityRuntime?.PumpHostSubscriptions();
        }

        if (!Multiplayer.Session.IsGameLoaded)
        {
            return;
        }

        Multiplayer.Session.PowerTowers.SendLocalStateIfChanged();
        Multiplayer.Session.FacilityDirectory.Update();

        gameResearchHashUpdateTimer += Time.deltaTime;
        dysonLaunchUpateTimer += Time.deltaTime;
        dysonSphereUpdateTimer += Time.deltaTime;
        warningUpdateTimer += Time.deltaTime;

        if (gameResearchHashUpdateTimer > GAME_RESEARCH_UPDATE_INTERVAL)
        {
            gameResearchHashUpdateTimer = 0;
            if (GameMain.data.history.currentTech != 0)
            {
                var state = GameMain.data.history.techStates[GameMain.data.history.currentTech];
                var hasActiveAutomaticResearch = ResearchStationStatistics.HasRecentPowerUsage(
                    GameMain.statistics.production.factoryStatPool, GameMain.data.factoryCount);
                SendPacket(new GameHistoryResearchUpdatePacket(GameMain.data.history.currentTech, state.hashUploaded,
                    state.hashNeeded, GameMain.statistics.techHashedFor10Frames, GameMain.data.history.techQueueLength,
                    hasActiveAutomaticResearch));
            }
        }

        if (dysonLaunchUpateTimer > LAUNCH_UPDATE_INTERVAL)
        {
            dysonLaunchUpateTimer = 0;
            Multiplayer.Session.Launch.SendBroadcastIfNeeded();
        }

        if (dysonSphereUpdateTimer > DYSONSPHERE_UPDATE_INTERVAL)
        {
            dysonSphereUpdateTimer = 0;
            Multiplayer.Session.DysonSpheres.UpdateSphereStatusIfNeeded();
        }

        if (!(warningUpdateTimer > WARNING_UPDATE_INTERVAL))
        {
            return;
        }

        warningUpdateTimer = 0;
        Multiplayer.Session.Warning.SendBroadcastIfNeeded();
    }

    private static void DisableNagleAlgorithm(WebSocketServer socketServer)
    {
        var listener = AccessTools.FieldRefAccess<WebSocketServer, TcpListener>("_listener")(socketServer);
        listener.Server.NoDelay = true;
    }
}
