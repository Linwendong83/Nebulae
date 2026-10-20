#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Players;

internal class PlayerMovementDisplayProcessor : PacketProcessor<PlayerMovementDisplay>
{
    protected override void ProcessPacket(PlayerMovementDisplay packet, NebulaeConnection conn)
    {
        // The host relays this and never receives it, so anything arriving here came from the host.
        if (IsHost || !packet.HasFinitePose())
        {
            return;
        }
        // A named planet that does not exist is a question about the world rather than the wire, and the
        // same guard the full-precision packet applies.
        if (packet.LocalPlanetId > 0 && GameMain.galaxy.PlanetById(packet.LocalPlanetId) == null)
        {
            return;
        }

        Multiplayer.Session.World.UpdateRemotePlayerRealtimeState(packet);
    }
}
