using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;

namespace NebulaeTests.Fixtures;

internal sealed class AckToHost : ISnapshotAckSink
{
    private readonly AuthoritySession host;

    public AckToHost(AuthoritySession host) => this.host = host;

    public void Send(AuthoritySnapshotAckPacket packet) =>
        TestAssert.IsTrue(host.NotifySnapshotAck(1, packet.BaselineId, packet.LastAppliedSequence,
            packet.Accepted));
}
