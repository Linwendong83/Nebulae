using HarmonyLib;
using NebulaeModel.Contracts;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets.GameStates;
using NebulaeModel.Packets.Factory;
using NebulaeModel.Packets.Factory.Belt;
using NebulaeModel.Packets.Session;
using NebulaeModel.Networking.Serialization;
using NebulaeNetwork;
using NebulaeWorld;
using NebulaeWorld.GameStates;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class JoinSyncRoutingTest
{
    private MultiplayerSession previous = null!;
    private Server server = null!;
    private JoinSyncManager joins = null!;

    [TestInitialize]
    public void SetUp()
    {
        previous = Multiplayer.Session;
        var session = Uninitialized<MultiplayerSession>();
        server = new Server(9869); joins = new JoinSyncManager();
        session.Server = server;
        AccessTools.Field(typeof(MultiplayerSession), "<JoinSync>k__BackingField").SetValue(session, joins);
        Multiplayer.Session = session;
    }

    [TestCleanup]
    public void TearDown() { joins.Dispose(); Multiplayer.Session = previous; }

    [TestMethod]
    public void StarPlanetAndExclusionRoutingAlsoReachOnlyMatchingJoiningPeers()
    {
        var local = Add(1, 101); var sameStar = Add(2, 102); var distant = Add(3, 201);
        server.SendPacketToStar(new BuildEntityRequest(101, 7, 1, 8), 1);
        TestAssert.HasCount(1, Drain(local)); TestAssert.HasCount(1, Drain(sameStar)); TestAssert.IsEmpty(Drain(distant));
        server.SendPacketToPlanet(new GamePausePacket(true), 101);
        TestAssert.HasCount(1, Drain(local)); TestAssert.IsEmpty(Drain(sameStar));
        server.SendPacketExclude(new GamePausePacket(false), sameStar);
        TestAssert.HasCount(1, Drain(local)); TestAssert.IsEmpty(Drain(sameStar)); TestAssert.HasCount(1, Drain(distant));
    }

    [TestMethod]
    public void BroadcastIsRetainedEvenWhenThereAreNoConnectedPlayers()
    {
        var joining = Add(1, 101);
        server.SendPacket(new GamePausePacket(true));
        TestAssert.HasCount(1, Drain(joining));
        TestAssert.IsEmpty(joining.Raw);
    }

    [TestMethod]
    public void DisconnectDropsOnlyThatConnectionsHistoryAndReusedPlayerIdStartsFresh()
    {
        var first = Add(1, 101); var second = Add(2, 101);
        server.SendPacket(new GamePausePacket(true));
        joins.Forget(first);
        TestAssert.IsFalse(Buffers.ContainsKey(first));
        TestAssert.HasCount(1, Drain(second));
        server.Players.TryRemove(first, out _);
        var reconnect = Add(3, 101);
        TestAssert.IsEmpty(Drain(reconnect));
        TestAssert.AreEqual(3L, Buffers[reconnect].Generation);
    }

    [TestMethod]
    public void ConnectedRecipientsContinueReceivingImmediatelyWhileANewPeerLoads()
    {
        var online = Add(1, 101);
        joins.Forget(online);
        server.Players.TryUpgrade(server.Players.Get(online, EConnectionStatus.Syncing), EConnectionStatus.Connected);
        var joining = Add(2, 101);
        server.SendPacket(new GamePausePacket(true));
        TestAssert.HasCount(1, online.Raw);
        TestAssert.IsEmpty(joining.Raw);
        TestAssert.HasCount(1, Drain(joining));
    }

    [TestMethod]
    public void AnOperationQueuedBehindTheJoinAckSeesAnAlreadyConnectedPlayer()
    {
        Multiplayer.Session.LocalPlayer = new LocalPlayer { Data = new PlayerData(0, 101), IsHost = true };
        var connection = Add(7, 101);
        joins.Loaded(connection, connection.Id, () =>
            server.Players.TryUpgrade(server.Players.Get(connection, EConnectionStatus.Syncing), EConnectionStatus.Connected));
        var codec = new NebulaeNetPacketProcessor();
        var handled = false;
        codec.SubscribeReusable<JoinSyncAck>(ack => joins.Acknowledge(connection, ack));
        codec.SubscribeReusable<GamePausePacket>(_ =>
        {
            TestAssert.IsNotNull(server.Players.Get(connection));
            TestAssert.IsNull(server.Players.Get(connection, EConnectionStatus.Syncing));
            handled = true;
        });
        codec.EnqueuePacketForProcessing(codec.Write(new JoinSyncAck { Generation = connection.Id, Sequence = 0 }), null);
        codec.EnqueuePacketForProcessing(codec.Write(new GamePausePacket()), null);
        codec.ProcessPacketQueue();
        TestAssert.IsTrue(handled);
        TestAssert.IsFalse(Buffers.ContainsKey(connection));
    }

    [TestMethod]
    public void ServerWideLimitRejectsOnlyTheOverflowingJoinAndDisconnectFreesCapacity()
    {
        // Broadcast recipients share immutable bytes, so this exercises the full logical
        // 256 MiB budget without allocating a quarter-gigabyte fixture.
        var bytes = new byte[1024 * 1024];
        var fill = new GamePausePacket(true);
        var connections = Enumerable.Range(1, 4).Select(id => Add(id, 101)).ToArray();
        foreach (var connection in connections)
            for (var i = 0; i < 64; i++) joins.Capture(connection, fill, bytes);
        TestAssert.AreEqual(JoinSyncBuffer.ServerLimit, joins.BufferedBytes);
        var overflow = Add(5, 101); var packet = new GamePausePacket(true); var raw = server.WriteOnce(packet);
        joins.Capture(overflow, packet, raw);
        TestAssert.IsTrue(Buffers[overflow].Failed);
        TestAssert.IsTrue(connections.All(c => !Buffers[c].Failed));
        joins.Forget(connections[0]);
        var replacement = Add(6, 101);
        joins.Capture(replacement, packet, raw);
        TestAssert.IsFalse(Buffers[replacement].Failed);
        TestAssert.AreEqual((long)raw.Length, Buffers[replacement].Bytes);
    }

    [TestMethod]
    public void CachedBudgetTracksCheckpointsAcknowledgementsAndDisconnects()
    {
        Multiplayer.Session.LocalPlayer = new LocalPlayer { Data = new PlayerData(0, 101), IsHost = true };
        var first = Add(1, 101); var second = Add(2, 101);
        var packet = new BuildEntityRequest(101, 7, 1, 8); var bytes = server.WriteOnce(packet);
        joins.Capture(first, packet, bytes); joins.Capture(second, packet, bytes);
        TestAssert.AreEqual(bytes.Length * 2L, joins.BufferedBytes);
        joins.Checkpoint(first, 101);
        TestAssert.AreEqual((long)bytes.Length, joins.BufferedBytes);
        joins.Capture(first, packet, bytes);
        joins.Loaded(first, first.Id, () => { });
        TestAssert.AreEqual(bytes.Length * 2L, joins.BufferedBytes, "Queued replay remains in flight.");
        joins.Acknowledge(first, new JoinSyncAck { Generation = first.Id, Sequence = Buffers[first].SentSequence });
        TestAssert.AreEqual((long)bytes.Length, joins.BufferedBytes);
        joins.Acknowledge(first, new JoinSyncAck { Generation = first.Id, Sequence = 1 });
        TestAssert.AreEqual((long)bytes.Length, joins.BufferedBytes, "Duplicate ACK cannot subtract twice.");
        joins.Forget(second); joins.Forget(second);
        TestAssert.AreEqual(0L, joins.BufferedBytes);
    }

    [TestMethod]
    public void RepeatedAbsoluteStateForOneObjectIsCapturedOnceAtItsLatestValue()
    {
        var joining = Add(1, 101);
        server.SendPacketToPlanet(new BeltSignalIconPacket(77, 1, 101), 101);
        server.SendPacketToPlanet(new BeltSignalIconPacket(77, 2, 101), 101);

        var captured = Drain(joining);
        TestAssert.HasCount(1, captured, "The belt icon is absolute state, so only its latest value is replayed.");

        var codec = new NebulaeNetPacketProcessor(); PacketUtils.RegisterNestedTypes(codec);
        BeltSignalIconPacket received = null!;
        codec.SubscribeReusable<BeltSignalIconPacket>(packet => received = packet);
        codec.ReadPacket(new NetDataReader(captured[0].Data));
        TestAssert.IsNotNull(received);
        TestAssert.AreEqual(2, received.SignalId);
    }

    private Dictionary<INebulaeConnection, JoinSyncBuffer> Buffers =>
        (Dictionary<INebulaeConnection, JoinSyncBuffer>)AccessTools.Field(typeof(JoinSyncManager), "buffers").GetValue(joins);
    private JoinSyncBuffer.Change[] Drain(Connection connection) => Buffers[connection].Drain().SelectMany(b => b).ToArray();
    private Connection Add(int id, int planet)
    {
        var connection = new Connection(id);
        var data = new PlayerData((ushort)id, planet) { LocalStarId = planet / 100 };
        server.Players.TryAdd(connection, new Player(connection, data));
        joins.Begin(connection); joins.Checkpoint(connection, 0);
        // Both planets of this star may already have factories on the client.
        joins.Checkpoint(connection, 101); joins.Checkpoint(connection, planet);
        return connection;
    }

    private sealed class Player(INebulaeConnection connection, IPlayerData data) : INebulaePlayer
    {
        public INebulaeConnection Connection { get; set; } = connection;
        public IPlayerData Data { get; set; } = data;
        public ushort Id => Data.PlayerId;
        public void SendPacket<T>(T packet) where T : class, new() => Connection.SendPacket(packet);
        public void LoadUserData(IPlayerData value) => Data = value;
    }
    private sealed class Connection(int id) : INebulaeConnection
    {
        public readonly List<byte[]> Raw = new();
        public bool IsAlive => true;
        public int Id => id;
        public EConnectionStatus ConnectionStatus { get; set; } = EConnectionStatus.Syncing;
        public void SendPacket<T>(T packet) where T : class, new() { }
        public void SendRawPacket(byte[] rawData) => Raw.Add(rawData);
        public bool Equals(INebulaeConnection? other) => ReferenceEquals(this, other);
    }
}