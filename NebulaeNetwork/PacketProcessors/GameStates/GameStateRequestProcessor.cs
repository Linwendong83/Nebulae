#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameStates;
using NebulaeWorld.GameStates;

#endregion

namespace NebulaeNetwork.PacketProcessors.GameStates;
internal class GameStateRequestProcessor : PacketProcessor<GameStateRequest>
{
    protected override void ProcessPacket(GameStateRequest packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            conn.SendPacket(
                new GameStateUpdate(packet.SentTimestamp, GameStatesManager.RealGameTick, GameStatesManager.RealUPS,
                    (float)FPSController.SimulateSpeed));
        }
        else
        {
            conn.SendPacket(packet);
        }
    }
}
