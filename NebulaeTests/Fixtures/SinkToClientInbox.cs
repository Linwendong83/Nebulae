using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;

namespace NebulaeTests.Fixtures;

internal sealed class SinkToClientInbox : IReplicationSink
{
    private readonly AuthoritySession client;
    public SinkToClientInbox(AuthoritySession client) => this.client = client;

    public void Send(ushort subscriberId, AuthorityEnvelopePacket packet)
    {
        var scope = packet switch
        {
            AuthorityLifecyclePacket lifecycle => new ScopeKey((PoolKind)lifecycle.ScopeKind, lifecycle.Scope),
            AuthorityWorldStatePacket worldState => new ScopeKey((PoolKind)worldState.ScopeKind, worldState.Scope),
            AuthoritySnapshotBeginPacket begin => new ScopeKey((PoolKind)begin.ScopeKind, begin.Scope),
            AuthoritySnapshotChunkPacket chunk => new ScopeKey((PoolKind)chunk.ScopeKind, chunk.Scope),
            AuthoritySnapshotCommitPacket commit => new ScopeKey((PoolKind)commit.ScopeKind, commit.Scope),
            _ => default
        };
        TestAssert.IsTrue(scope.IsValid);
        TestAssert.IsTrue(client.TryEnqueueReplicaMessage(packet, new ApplyScope(scope, 0, packet.Sequence, packet.HostTick)));
    }
}
