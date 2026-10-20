using NebulaeModel.Networking;
using NebulaeModel.Authority;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority;

namespace NebulaeNetwork.PacketProcessors.Authority;
internal sealed class PlayerCombatStateProcessor : PacketProcessor<AuthorityPlayerCombatStatePacket>
{
    protected override void ProcessPacket(AuthorityPlayerCombatStatePacket packet, NebulaeConnection conn)
    {
        if (IsHost || !AuthorityPacketAdmission.Admit(packet, AuthorityDirection.ServerToClient, conn, 0, out _)) return;
        Multiplayer.Session.AuthorityRuntime.TryEnqueueReplicaMessage(packet,
            new ApplyScope(new ScopeKey(PoolKind.PlayerCombat, 0), hostTick: packet.HostTick));
    }
}
