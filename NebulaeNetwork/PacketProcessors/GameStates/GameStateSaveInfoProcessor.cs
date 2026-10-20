#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameStates;
using NebulaeWorld.GameStates;

#endregion

namespace NebulaeNetwork.PacketProcessors.GameStates;
internal class GameStateSaveInfoProcessor : PacketProcessor<GameStateSaveInfoPacket>
{
    protected override void ProcessPacket(GameStateSaveInfoPacket packet, NebulaeConnection conn)
    {
        GameStatesManager.LastSaveTime = packet.LastSaveTime;
    }
}
