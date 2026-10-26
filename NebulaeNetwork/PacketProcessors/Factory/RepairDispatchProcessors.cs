using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Factory;

internal sealed class RepairOfferProcessor : PacketProcessor<RepairOfferPacket>
{
    protected override void ProcessPacket(RepairOfferPacket packet, NebulaeConnection conn)
    {
        if (!IsHost) return;
        var player = Players.Get(conn);
        if (player?.Data.LocalPlanetId == packet.PlanetId) Multiplayer.Session.Repairs.ReceiveOffer(player.Id, packet);
    }
}
internal sealed class RepairControlProcessor : PacketProcessor<RepairControlPacket>
{
    protected override void ProcessPacket(RepairControlPacket packet, NebulaeConnection conn)
    {
        if (!IsHost) { Multiplayer.Session.Repairs.ReceiveControl(0, packet); return; }
        var player = Players.Get(conn);
        if (player != null) Multiplayer.Session.Repairs.ReceiveControl(player.Id, packet);
    }
}
internal sealed class RepairDroneStateProcessor : PacketProcessor<RepairDroneStatePacket>
{
    protected override void ProcessPacket(RepairDroneStatePacket packet, NebulaeConnection conn)
    {
        if (!IsHost) Multiplayer.Session.Repairs.ReceiveState(packet);
    }
}
