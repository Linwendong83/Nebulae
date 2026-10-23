#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Players;
internal class PlayerEjectMechaDroneProcessor : PacketProcessor<PlayerEjectMechaDronePacket>
{
    protected override void ProcessPacket(PlayerEjectMechaDronePacket packet, NebulaeConnection conn)
    {
        if (packet.TargetObjectId <= 0) return;
        if (IsHost)
        {
            var sender = Players.Get(conn);
            if (sender == null || sender.Id != packet.PlayerId || sender.Data.LocalPlanetId != packet.PlanetId)
                return;
        }
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        if (factory == null || packet.TargetObjectId >= factory.entityCursor ||
            factory.entityPool[packet.TargetObjectId].id != packet.TargetObjectId) return;

        Multiplayer.Session.Drones.EjectMechaDroneFromOtherPlayer(packet.PlayerId, packet.PlanetId,
            packet.TargetObjectId, 0, 0, 0, packet.DronePriority);
    }
}
