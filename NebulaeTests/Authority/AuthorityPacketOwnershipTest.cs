
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// the transport registers every packet processor through
/// <c>SubscribeReusable</c>, so it deserializes each incoming packet into the same instance per
/// type. A processor that queues the packet (the queued packet handler) must therefore own a copy, or the
/// next packet of the same type rewrites the queued one before the frame boundary applies it.
/// This was the actual cause of queued snapshots/digests applying under a stale scope.
/// </summary>
[TestClass]
public class AuthorityPacketOwnershipTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA220062200000020, 0xA220062200000021);
    private static readonly ScopeKey Scope = new(PoolKind.GroundEnemy, 102);

    [TestMethod]
    public void AnOwnedCopyKeepsItsValueFieldsWhenTheSourceIsReused()
    {
        var header = new AuthorityEnvelopeHeader(AuthorityFamily.SnapshotBegin,
            Epoch, new ConnectionEpoch(5), sequence: 1, hostTick: 10, claimedPlayerId: 0, payloadLength: 0);
        var source = AuthoritySnapshotBeginPacket.Create(header, Scope, baselineId: 7, subscriptionEpoch: 3,
            cutoffSequence: 0, totalBytes: 4, chunkCount: 1, snapshotHash: 9);

        var owned = (AuthoritySnapshotBeginPacket)source.CreateOwnedCopy();

        // The transport "reuses" the instance: overwrite every field the way a new packet would.
        source.Scope = 999;
        source.ScopeKind = (byte)PoolKind.Entity;
        source.BaselineId = 0;
        source.SubscriptionEpoch = 0;

        TestAssert.AreEqual(102, owned.Scope);
        TestAssert.AreEqual((byte)PoolKind.GroundEnemy, owned.ScopeKind);
        TestAssert.AreEqual(7L, owned.BaselineId);
        TestAssert.AreEqual(3L, owned.SubscriptionEpoch);
    }

    [TestMethod]
    public void AnOwnedCopyDeepCopiesItsPayloadBuffer()
    {
        var header = new AuthorityEnvelopeHeader(AuthorityFamily.SnapshotChunk,
            Epoch, new ConnectionEpoch(5), sequence: 1, hostTick: 10, claimedPlayerId: 0, payloadLength: 0);
        var source = AuthoritySnapshotChunkPacket.Create(header, Scope, baselineId: 7, subscriptionEpoch: 3,
            chunkIndex: 0, data: [1, 2, 3]);
        var owned = (AuthoritySnapshotChunkPacket)source.CreateOwnedCopy();
        source.Data[0] = 99;
        TestAssert.AreEqual((byte)1, owned.Data[0], "The copy owns its buffer, not the reused one.");
    }

    private sealed class RecordingExecutor : IHostCommandExecutor
    {
        public readonly List<byte[]> Payloads = [];

        public CommandOutcome Execute(in QueuedHostCommand command)
        {
            Payloads.Add(command.Packet.Payload);
            return new CommandOutcome(CommandResultCode.Applied, appliedHostTick: 1, transactionId: 1);
        }
    }

    [TestMethod]
    public void AQueuedCommandKeepsItsPayloadWhenTheSourceInstanceIsReused()
    {
        var connection = new ConnectionEpoch(5);
        var queue = new HostCommandQueue(Epoch);
        var header = new AuthorityEnvelopeHeader(AuthorityFamily.Command,
            Epoch, connection, sequence: 1, hostTick: 0, claimedPlayerId: 0, payloadLength: 0);
        var target = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 102, 1, 1);
        var source = AuthorityCommandPacket.Create(header, target, category: 1, payload: [7, 7, 7]);

        TestAssert.IsTrue(queue.TryEnqueue(new CommandKey(Epoch, connection, 1), source, 9, 0, 0));
        // The transport reuses the instance before the drain.
        source.Payload[0] = 200;

        var executor = new RecordingExecutor();
        queue.Drain(executor, null);
        TestAssert.HasCount(1, executor.Payloads);
        TestAssert.AreEqual((byte)7, executor.Payloads[0][0],
            "The queued command must own its payload, not share the reused packet's buffer.");
    }
}
