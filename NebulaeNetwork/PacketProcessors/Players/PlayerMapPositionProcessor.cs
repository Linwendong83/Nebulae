#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Players;

internal class PlayerMapPositionProcessor : PacketProcessor<PlayerMapPosition>
{
    protected override void ProcessPacket(PlayerMapPosition packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            return;
        }

        // A player in another system is only observable as a dot on the star map, so their position is
        // written straight through instead of through the interpolation buffer: there is nothing to
        // interpolate at one update a second.
        using (Multiplayer.Session.World.GetRemotePlayersModels(out var remotePlayersModels))
        {
            if (!remotePlayersModels.TryGetValue(packet.PlayerId, out var model))
            {
                return;
            }
            model.Movement.UpdateMapPosition(packet);
        }
    }
}
