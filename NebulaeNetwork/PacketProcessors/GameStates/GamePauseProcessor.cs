#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameStates;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.GameStates;

internal class GamePauseProcessor : PacketProcessor<GamePausePacket>
{
    protected override void ProcessPacket(GamePausePacket packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            Multiplayer.Session.World.HostSetWorldPause(packet.Paused);
        }
        else
        {
            Multiplayer.Session.World.SetWorldPause(packet.Paused);
        }
    }
}
