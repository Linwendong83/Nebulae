using NebulaeModel.Contracts;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Players;
using NebulaeWorld.MonoBehaviours.Remote;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class PlayerMovementRelayTest
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HostAndRemoteMovementReachOnlyTheRequiredRecipients(bool remote)
    {
        var network = new RecordingNetwork();
        var sender = network.Add(1, 1);
        var nearby = network.Add(2, 1);
        var distant = network.Add(3, 2);
        var relay = new PlayerMovementRelay();
        var display = new PlayerMovementDisplay { PlayerId = 1, LocalPlanetId = 101, UPosition = new Double3(1, 2, 3) };
        // A listen host is not a connection in the recipient collection.
        if (!remote) network.Players.Remove(sender);
        for (var tick = 0; tick < 20; tick++)
            relay.Send(network, display, 1, tick / 10f, remote ? sender.Connection : null);

        TestAssert.HasCount(20, nearby.Packets);
        TestAssert.IsTrue(nearby.Packets.All(p => p is PlayerMovementDisplay));
        TestAssert.HasCount(2, distant.Packets);
        TestAssert.IsTrue(distant.Packets.All(p => p is PlayerMapPosition));
        TestAssert.IsEmpty(sender.Packets);
        var map = (PlayerMapPosition)distant.Packets.Last();
        TestAssert.AreEqual(display.PlayerId, map.PlayerId);
        TestAssert.AreEqual(display.LocalPlanetId, map.LocalPlanetId);
        TestAssert.AreEqual(1, map.LocalStarId);
        TestAssert.AreEqual(display.UPosition.x, map.UPosition.x);

        var processor = new NebulaeNetPacketProcessor();
        PacketUtils.RegisterNestedTypes(processor);
        var fullBytes = PacketCompression.WrapForSend(processor.Write(display)).Length * 10;
        var mapBytes = PacketCompression.WrapForSend(processor.Write(map)).Length;
        TestAssert.IsLessThan(fullBytes / 10, mapBytes, "Distant traffic should be under a tenth of the former broadcast.");
        Console.WriteLine($"Movement per distant peer: {fullBytes} -> {mapBytes} framed bytes/second at 10 samples/second.");
    }

    [TestMethod]
    public void StarPlanetAndClockTransitionsBypassTheMapThrottle()
    {
        var network = new RecordingNetwork();
        var oldStar = network.Add(2, 1);
        var newStar = network.Add(3, 2);
        var distant = network.Add(4, 3);
        var relay = new PlayerMovementRelay();
        var display = new PlayerMovementDisplay { PlayerId = 1, LocalPlanetId = 101 };
        relay.Send(network, display, 1, 10);
        foreach (var player in network.Players) player.Packets.Clear();

        display.LocalPlanetId = 201;
        relay.Send(network, display, 2, 10.1f);
        TestAssert.IsInstanceOfType<PlayerMapPosition>(oldStar.Packets.Single());
        TestAssert.IsInstanceOfType<PlayerMovementDisplay>(newStar.Packets.Single());
        TestAssert.HasCount(1, distant.Packets);

        display.LocalPlanetId = 202;
        relay.Send(network, display, 2, 10.2f);
        relay.Send(network, display, 2, 0); // new clock origin
        relay.Send(network, display, 2, .5f);
        relay.Send(network, display, 2, 1f);
        TestAssert.HasCount(4, distant.Packets);
    }

    private sealed class Peer(ushort id, int star) : INebulaePlayer
    {
        public List<object> Packets { get; } = new();
        public INebulaeConnection Connection { get; set; } = new Connection();
        public IPlayerData Data { get; set; } = new PlayerData { PlayerId = id, LocalStarId = star };
        public ushort Id => Data.PlayerId;
        public void SendPacket<T>(T packet) where T : class, new() => Packets.Add(packet);
        public void LoadUserData(IPlayerData data) => Data = data;
    }

    private sealed class Connection : INebulaeConnection
    {
        public bool IsAlive => true;
        public int Id => 1;
        public EConnectionStatus ConnectionStatus { get; set; }
        public void SendPacket<T>(T packet) where T : class, new() => throw new NotSupportedException();
        public void SendRawPacket(byte[] data) => throw new NotSupportedException();
        public bool Equals(INebulaeConnection? other) => ReferenceEquals(this, other);
    }

    private sealed class RecordingNetwork : INetworkProvider
    {
        public List<Peer> Players { get; } = new();
        public Peer Add(ushort id, int star) { var peer = new Peer(id, star); Players.Add(peer); return peer; }
        public INetPacketProcessor PacketProcessor => throw new NotSupportedException();
        public byte[] WriteOnce<T>(T packet) where T : class, new() => throw new NotSupportedException();
        public void SendToMatching<T>(T packet, Predicate<INebulaePlayer> condition) where T : class, new()
        { foreach (var peer in Players) if (condition(peer)) peer.SendPacket(packet); }
        public void SendPacketToStarExclude<T>(T packet, int star, INebulaeConnection exclude) where T : class, new() =>
            SendToMatching(packet, p => p.Data.LocalStarId == star && !p.Connection.Equals(exclude));
        public void SendPacket<T>(T packet) where T : class, new() => throw new NotSupportedException("Unexpected global broadcast");
        public void SendPacketToLocalStar<T>(T packet) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToLocalPlanet<T>(T packet) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToPlanet<T>(T packet, int planet) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToStar<T>(T packet, int star) where T : class, new() => throw new NotSupportedException();
        public void SendPacketExclude<T>(T packet, INebulaeConnection exclude) where T : class, new() => throw new NotSupportedException();
        public void Dispose() { }
    }
}

[TestClass]
public class RemotePlayerPresentationTest
{
    [TestMethod]
    public void ANewerMapPositionWinsOverAStalePoseSnapshot()
    {
        var movement = Movement(Pose(100, 101, new Double3(1, 2, 3)), mapTimestamp: 200,
            mapPlanet: 202, mapPosition: new VectorLF3(7, 8, 9));

        TestAssert.IsTrue(movement.TryGetStarmapPose(out var position, out var planet, out _));
        TestAssert.AreEqual(7d, position.x);
        TestAssert.AreEqual(8d, position.y);
        TestAssert.AreEqual(9d, position.z);
        TestAssert.AreEqual(202, planet);
    }

    [TestMethod]
    public void ANewerPoseSnapshotWinsOverAStaleMapPosition()
    {
        var movement = Movement(Pose(300, -1, new Double3(1, 2, 3)), mapTimestamp: 200,
            mapPlanet: 202, mapPosition: new VectorLF3(7, 8, 9));

        TestAssert.IsTrue(movement.TryGetStarmapPose(out var position, out var planet, out _));
        TestAssert.AreEqual(1d, position.x);
        TestAssert.AreEqual(2d, position.y);
        TestAssert.AreEqual(3d, position.z);
        TestAssert.AreEqual(-1, planet);
    }

    [TestMethod]
    public void APlayerWithoutEitherStreamHasNoStarmapPosition()
    {
        var movement = Uninitialized<RemotePlayerMovement>();
        SetField(movement, "snapshotBuffer", new[] { default(RemotePlayerMovement.Snapshot), default(RemotePlayerMovement.Snapshot), default(RemotePlayerMovement.Snapshot) });
        SetField(movement, "hasMapPosition", false);

        TestAssert.IsFalse(movement.TryGetStarmapPose(out _, out _, out _));
    }

    private static RemotePlayerMovement Movement(RemotePlayerMovement.Snapshot pose, long mapTimestamp,
        int mapPlanet, VectorLF3 mapPosition)
    {
        var movement = Uninitialized<RemotePlayerMovement>();
        SetField(movement, "snapshotBuffer", new[] { pose, pose, pose });
        SetField(movement, "hasMapPosition", true);
        SetField(movement, "mapTimestamp", mapTimestamp);
        SetField(movement, "mapLocalPlanetId", mapPlanet);
        SetField(movement, "mapAbsolutePosition", mapPosition);
        return movement;
    }

    private static RemotePlayerMovement.Snapshot Pose(long timestamp, int planetId, Double3 uPosition) => new()
    {
        Timestamp = timestamp,
        LocalPlanetId = planetId,
        UPosition = uPosition
    };
}
