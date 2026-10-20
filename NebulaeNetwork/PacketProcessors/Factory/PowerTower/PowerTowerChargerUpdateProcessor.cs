using System.Linq;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.PowerTower;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Factory.PowerTower;
internal class PowerTowerChargerUpdateProcessor : PacketProcessor<PowerTowerChargerUpdate>
{
    protected override void ProcessPacket(PowerTowerChargerUpdate packet, NebulaeConnection conn)
    {
        if (packet.NodeIds == null) return;

        var towers = Multiplayer.Session.PowerTowers;
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player == null) return;

            // Attribute the snapshot to the connection, and reject towers already removed on the host.
            var powerSystem = packet.PlanetId > 0 ? GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.powerSystem : null;
            var nodes = powerSystem == null ? [] : packet.NodeIds.Where(id => id > 0 &&
                id < powerSystem.nodeCursor && id < powerSystem.nodePool.Length &&
                powerSystem.nodePool[id].id == id && powerSystem.nodePool[id].isCharger &&
                powerSystem.nodePool[id].coverRadius <= 20f).ToArray();
            var previous = towers.GetPlayerState(player.Id);
            var snapshot = new PowerTowerChargerUpdate(player.Id, packet.PlanetId, nodes);
            if (towers.ApplyRemoteState(snapshot))
            {
                // Audience is the union of the planet being left and the planet being entered: a peer
                // simulates exactly one planet's power network, so only those peers' charger counts
                // move, and a peer elsewhere never touches these nodes. The author is excluded
                // because its own simulation already produced the snapshot.
                // Replacement is planet-qualified, so a peer on the old
                // planet retires its count from the same packet that seeds the new one.
                var to = snapshot.PlanetId;
                var from = previous.PlanetId;
                Server.SendToMatching(towers.GetPlayerState(player.Id), p =>
                    p.Id != player.Id && p.Data.LocalPlanetId > 0 &&
                    (p.Data.LocalPlanetId == to || p.Data.LocalPlanetId == from));
            }
            return;
        }

        // Keep membership even if this client has not loaded the corresponding factory yet.
        towers.ApplyRemoteState(packet);
    }
}
