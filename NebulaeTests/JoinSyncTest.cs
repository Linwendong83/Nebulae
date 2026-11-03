using NebulaeModel.Networking;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory;
using NebulaeModel.Packets.GameHistory;
using NebulaeModel.Packets.GameStates;
using NebulaeModel.Packets.Routers;
using NebulaeModel.Packets.Session;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class JoinSyncTest
{
    [TestMethod]
    public void WorldCutCarriesFreshMechaBonusesInsteadOfTheEarlierHandshakeValues()
    {
        var codec = new NebulaeNetPacketProcessor(); PacketUtils.RegisterNestedTypes(codec);
        JoinSyncBegin? received = null;
        codec.SubscribeReusable<JoinSyncBegin>(p => received = p);
        var cut = new JoinSyncBegin
        {
            Generation = 12,
            TechBonuses = new PlayerTechBonuses
            { thrusterLevel = 2, inventorySize = 80, coreEnergyCap = 123456789 }
        };
        codec.DispatchInline(codec.Write(cut), null);
        TestAssert.IsNotNull(received);
        TestAssert.AreEqual(2, received.TechBonuses.thrusterLevel);
        TestAssert.AreEqual(80, received.TechBonuses.inventorySize);
        TestAssert.AreEqual(123456789d, received.TechBonuses.coreEnergyCap);
    }

    [TestMethod]
    public void SnapshotsExcludeEarlierOperationsAndPreserveLaterGlobalAndFactoryChanges()
    {
        var buffer = new JoinSyncBuffer(17, 0);
        buffer.Append(0, new byte[] { 1 });
        buffer.Checkpoint(0, 1);
        buffer.Append(0, new byte[] { 2 });
        buffer.Append(101, new byte[] { 3 });
        buffer.Append(-101, new byte[] { 4 });
        buffer.Append(-201, new byte[] { 5 });
        buffer.Checkpoint(101, 2);
        buffer.Append(101, new byte[] { 6 });
        buffer.Append(-101, new byte[] { 7 });
        buffer.BeginReplay(3);
        CollectionAssert.AreEqual(new byte[] { 2, 5, 6, 7 }, buffer.Drain().SelectMany(b => b).Select(c => c.Data[0]).ToArray());
        TestAssert.AreEqual(4L, buffer.SentSequence);
    }

    [TestMethod]
    public void AcknowledgementReleasesOnlySentBytesAndRejectsDuplicateOrOtherGeneration()
    {
        var buffer = new JoinSyncBuffer(17, 0);
        buffer.Checkpoint(0, 0);
        buffer.Append(0, new byte[10]);
        buffer.BeginReplay(1);
        buffer.Drain();
        buffer.Append(0, new byte[20]);
        TestAssert.AreEqual(30L, buffer.Bytes);
        TestAssert.IsFalse(buffer.Acknowledge(16, 1, 2));
        TestAssert.IsFalse(buffer.Acknowledge(17, 2, 2));
        TestAssert.IsTrue(buffer.Acknowledge(17, 1, 2));
        TestAssert.IsFalse(buffer.Acknowledge(17, 1, 3));
        TestAssert.AreEqual(20L, buffer.Bytes);
        TestAssert.HasCount(20, buffer.Drain().Single().Single().Data);
        TestAssert.AreEqual(2L, buffer.SentSequence);
    }

    [TestMethod]
    public void EmptyReplayStillRequiresOneAcknowledgement()
    {
        var buffer = new JoinSyncBuffer(17, 0);
        buffer.Checkpoint(0, 0); buffer.BeginReplay(1);
        TestAssert.IsEmpty(buffer.Drain());
        TestAssert.IsTrue(buffer.Acknowledge(17, 0, 2));
        TestAssert.IsFalse(buffer.Acknowledge(17, 0, 3));
    }

    [TestMethod]
    public void AcknowledgingAnAppliedPrefixDoesNotWaitForTheWorldToStopChanging()
    {
        var buffer = new JoinSyncBuffer(4, 0); buffer.Checkpoint(0, 0);
        buffer.Append(0, new byte[10]); buffer.BeginReplay(1); buffer.Drain(); buffer.SealInitialReplay();
        buffer.Append(0, new byte[20]); buffer.Drain();
        TestAssert.IsFalse(buffer.Acknowledge(4, 0, 2), "The initial replay must be covered.");
        TestAssert.IsTrue(buffer.Acknowledge(4, 1, 2));
        TestAssert.AreEqual(20L, buffer.Bytes, "Newer in-flight changes still count against the limit.");
        TestAssert.IsTrue(buffer.Acknowledge(4, 2, 3));
        TestAssert.AreEqual(0L, buffer.Bytes);
    }

    [TestMethod]
    public void LimitsCountInFlightBytesAndOtherPlayersRemainIndependent()
    {
        var first = new JoinSyncBuffer(1, 0); var second = new JoinSyncBuffer(2, 0);
        first.Checkpoint(0, 0); second.Checkpoint(0, 0);
        first.Append(0, new byte[8], 10); first.BeginReplay(1); first.Drain();
        TestAssert.IsFalse(first.Append(0, new byte[3], 10));
        TestAssert.IsTrue(first.Expired(2));
        TestAssert.IsTrue(second.Append(0, new byte[3], 10));
        TestAssert.IsFalse(second.Expired(299));
        TestAssert.IsTrue(second.Expired(300));
    }

    [TestMethod]
    public void OnlyNewProgressFromTheSameConnectionResetsTheIdleDeadline()
    {
        var buffer = new JoinSyncBuffer(1, 0); buffer.Checkpoint(0, 0);
        buffer.ReportProgress(2, 100, 250);
        TestAssert.IsTrue(buffer.Expired(300));
        buffer.ReportProgress(1, 100, 250);
        TestAssert.IsFalse(buffer.Expired(300));
        buffer.ReportProgress(1, 100, 500);
        TestAssert.IsTrue(buffer.Expired(550));
        buffer.ReportProgress(1, 101, 501);
        TestAssert.IsFalse(buffer.Expired(550));
    }

    [TestMethod]
    public void BatchesBoundWorkAndRetainOrder()
    {
        var buffer = new JoinSyncBuffer(1, 0); buffer.Checkpoint(0, 0);
        for (var i = 0; i < 600; i++) buffer.Append(0, BitConverter.GetBytes(i));
        var batches = buffer.Drain();
        TestAssert.HasCount(3, batches);
        TestAssert.IsTrue(batches.All(b => b.Length <= JoinSyncBuffer.BatchCount));
        CollectionAssert.AreEqual(Enumerable.Range(0, 600).ToArray(), batches.SelectMany(b => b).Select(c => BitConverter.ToInt32(c.Data, 0)).ToArray());
    }

    [TestMethod]
    public void EveryRuntimePacketHasAnExplicitJoinPolicy()
    {
        var policy = new JoinPacketPolicy();
        foreach (var processor in RuntimePacketRegistry.ProcessorTypes)
        {
            var type = processor;
            while (type.BaseType != null && (!type.BaseType.IsGenericType || type.BaseType.GetGenericTypeDefinition() != typeof(PacketProcessor<>))) type = type.BaseType;
            var packet = type.BaseType!.GetGenericArguments()[0];
            TestAssert.IsTrue(policy.Contains(packet), packet.FullName);
        }
    }

    [TestMethod]
    public void NestedRouterChildrenAreClassifiedIndividuallyAndControlCannotBeReplayed()
    {
        var codec = new NebulaeNetPacketProcessor(); PacketUtils.RegisterNestedTypes(codec);
        var policy = new JoinPacketPolicy();
        var build = new BuildEntityRequest(101, 3, 1, 4);
        var research = new GameHistoryUnlockTechPacket(1001, 1);
        var router = new StarBroadcastPacket(new[] { codec.Write(build), codec.Write(new PlanetBroadcastPacket(codec.Write(research), 101)) }, 1);
        var changes = policy.Describe(router, codec.Write(router));
        CollectionAssert.AreEqual(new[] { 101, 0 }, changes.Select(c => c.Scope).ToArray());
        TestAssert.IsTrue(policy.CanReplay(changes[0].Data, 101));
        TestAssert.IsFalse(policy.CanReplay(changes[0].Data, 201));
        TestAssert.IsFalse(policy.CanReplay(codec.Write(new JoinSyncAck()), 0));
    }

    [TestMethod]
    public void ResearchPauseAndResumeAreStateChangesButOneKeyUnlockIsAnIntent()
    {
        var policy = new JoinPacketPolicy(); var codec = new NebulaeNetPacketProcessor();
        foreach (var kind in new[] { GameHistoryEvent.PauseQueue, GameHistoryEvent.ResumeQueue, GameHistoryEvent.OneKeyUnlock })
        {
            var packet = new GameHistoryNotificationPacket(kind);
            var changes = policy.Describe(packet, codec.Write(packet));
            TestAssert.HasCount(kind == GameHistoryEvent.OneKeyUnlock ? 0 : 1, changes);
        }
    }

    [TestMethod]
    public void PlayerDepartureAfterTheInitialRosterIsNotLostWhileCatchingUp()
    {
        var policy = new JoinPacketPolicy(); var codec = new NebulaeNetPacketProcessor();
        var packet = new PlayerDisconnected(7, 2);
        var changes = policy.Describe(packet, codec.Write(packet));
        TestAssert.HasCount(1, changes);
        TestAssert.IsTrue(policy.CanReplay(changes[0].Data, 0));
    }

    [TestMethod]
    public void RouterExpansionCannotBeOvertakenByABarrierAlreadyInTheQueue()
    {
        var codec = new NebulaeNetPacketProcessor(); var order = new List<string>();
        codec.SubscribeReusable<GamePausePacket>(_ => order.Add("child"));
        codec.SubscribeReusable<JoinSyncBarrier>(_ => order.Add("barrier"));
        codec.SubscribeReusable<StarBroadcastPacket>(p => { foreach (var child in p.PacketObjects) codec.DispatchInline(child, null); });
        var nested = new StarBroadcastPacket(codec.Write(new GamePausePacket(true)), 1);
        codec.EnqueuePacketForProcessing(codec.Write(new StarBroadcastPacket(codec.Write(nested), 1)), null);
        codec.EnqueuePacketForProcessing(codec.Write(new JoinSyncBarrier()), null);
        codec.ProcessPacketQueue();
        CollectionAssert.AreEqual(new[] { "child", "barrier" }, order);
    }

    [TestMethod]
    public void TheLatestAbsoluteStateReplacesItsEarlierChangeBehindTheEventsThatFollowed()
    {
        var buffer = new JoinSyncBuffer(17, 0);
        buffer.Checkpoint(101, 0);
        buffer.Append(101, new byte[] { 1 }, key: "object:7");
        buffer.Append(101, new byte[] { 9 });
        buffer.Append(101, new byte[] { 2 }, key: "object:7");

        var changes = buffer.Drain().SelectMany(batch => batch).ToArray();
        CollectionAssert.AreEqual(new byte[] { 9, 2 }, changes.Select(change => change.Data[0]).ToArray());
        TestAssert.AreEqual(2L, buffer.Bytes, "Only the replacement and the event remain in flight.");
    }

    [TestMethod]
    public void EventChangesAreNeverCoalescedEvenWhenTheyShareAScope()
    {
        var buffer = new JoinSyncBuffer(17, 0);
        buffer.Checkpoint(101, 0);
        buffer.Append(101, new byte[] { 1 });
        buffer.Append(101, new byte[] { 2 });

        var changes = buffer.Drain().SelectMany(batch => batch).ToArray();
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, changes.Select(change => change.Data[0]).ToArray());
    }

    [TestMethod]
    public void AnOverflowingBacklogFailsAtTheHardLimitWithoutPausingTheWorld()
    {
        var buffer = new JoinSyncBuffer(17, 0);
        buffer.Checkpoint(0, 0);

        TestAssert.IsTrue(buffer.Append(0, new byte[7], 10));
        TestAssert.IsFalse(buffer.Append(0, new byte[4], 10));
        TestAssert.IsTrue(buffer.Failed);
    }
}