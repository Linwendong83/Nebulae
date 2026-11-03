using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class TurretReplicationTest
{
    private static readonly AuthorityEpoch Epoch = new(77, 88);
    private static readonly ScopeKey Scope = new(PoolKind.Turret, 101);
    private static ObjectKey Key(long generation) => ObjectKey.Create(Epoch, PoolKind.Turret, 101, 9, generation);
    private static byte[] State(short bullets)
    {
        TestAssert.IsTrue(TurretStateCodec.TryEncode(new TurretState { EntityId = 9, ModelIndex = 400, Type = 1, BulletCount = bullets }, out var bytes));
        return bytes;
    }
    private sealed class View : IHostWorldView
    {
        public ObjectKey Key = TurretReplicationTest.Key(1);
        public byte[] State = TurretReplicationTest.State(30);
        public bool Alive = true;
        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        { if (scope != Scope) return false; if (Alive) members.Add(Key); return true; }
        public bool TryReadState(ObjectKey key, out byte[] state) { state = State; return Alive && key == Key; }
    }
    private sealed class Observer : IReplicaMirrorObserver
    {
        public bool Ready;
        public TurretState Applied;
        public int Writes;
        public readonly TurretMirror Mirror;
        public Observer() => Mirror = new TurretMirror(Epoch, (_, state) =>
        { if (!Ready) return false; Applied = state; Writes++; return true; }, _ => Applied = default);
        public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] bytes)
        { TestAssert.IsTrue(TurretStateCodec.TryDecode(bytes, out var state)); TestAssert.IsTrue(Mirror.Put(key, state)); }
        public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
        {
            var decoded = new List<(ObjectKey, TurretState)>();
            foreach (var member in members) { TestAssert.IsTrue(TurretStateCodec.TryDecode(member.State, out var state)); decoded.Add((member.Key, state)); }
            Mirror.Install(scope.Scope, decoded);
        }
        public void OnMemberRemoved(ScopeKey scope, in ObjectKey key) => Mirror.Remove(key);
    }
    private sealed class Sink : IReplicationSink
    {
        private readonly AuthoritySession client;
        public Sink(AuthoritySession client) { this.client = client; }
        public void Send(ushort subscriber, AuthorityEnvelopePacket packet) =>
            TestAssert.IsTrue(client.TryEnqueueReplicaMessage(packet, new ApplyScope(Scope, 0, packet.Sequence, packet.HostTick)));
    }
    private sealed class Ack : ISnapshotAckSink
    {
        private readonly AuthoritySession host;
        public Ack(AuthoritySession host) { this.host = host; }
        public void Send(AuthoritySnapshotAckPacket packet) => TestAssert.IsTrue(host.NotifySnapshotAck(1, packet.BaselineId, packet.LastAppliedSequence, packet.Accepted));
    }
    [TestMethod]
    public void BaselineIncrementalReuseAndDespawnCrossTheRealReplicationPipeline()
    {
        var host = new AuthoritySession(new AuthoritySessionState()); host.BeginAuthorityWorld(Epoch, true);
        var client = new AuthoritySession(new AuthoritySessionState()); client.BeginAuthorityWorld(Epoch, false);
        var view = new View(); var observer = new Observer();
        host.RegisterHostReplication(view, new Sink(client)); client.WorldReplica.MirrorObserver = observer;
        host.HostReplicator.EngagementProvider = _ => false;
        host.HostReplicator.ColdStateIntervalTicks = 1000;
        host.HostReplicator.EventDrivenStateFor = key => key.Kind == PoolKind.Turret;
        client.WorldReplica.SnapshotAckSink = new Ack(host);
        client.WorldReplica.Subscribe(Scope); host.HostReplicator.Subscribe(1, Scope);
        host.OnFrameBoundary(100); client.OnFrameBoundary(100); host.OnFrameBoundary(110); client.OnFrameBoundary(110);
        TestAssert.IsTrue(client.WorldReplica.Versions.TryGetScope(Scope, out var state)); TestAssert.AreEqual(SubscriptionPhase.Live, state.Phase);
        TestAssert.AreEqual(1, observer.Mirror.PendingCount); TestAssert.AreEqual(0, observer.Writes);
        observer.Ready = true; observer.Mirror.Retry(101); TestAssert.AreEqual((short)30, observer.Applied.BulletCount);
        view.State = State(27); host.Capture.Capture(121); client.OnFrameBoundary(121);
        TestAssert.AreEqual((short)27, observer.Applied.BulletCount);
        view.Key = Key(2); view.State = State(60); host.Capture.Capture(130); client.OnFrameBoundary(130);
        TestAssert.AreEqual((short)60, observer.Applied.BulletCount);
        view.Alive = false; host.Capture.Capture(140); client.OnFrameBoundary(140);
        TestAssert.AreEqual(0, observer.Applied.EntityId); TestAssert.AreEqual(0, observer.Mirror.PendingCount);
    }
}
