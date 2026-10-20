#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameStates;
using NebulaeWorld.GameStates;

#endregion

namespace NebulaeNetwork.PacketProcessors.GameStates;
internal class FragmentInfoProcessor : PacketProcessor<FragmentInfo>
{
    protected override void ProcessPacket(FragmentInfo packet, NebulaeConnection conn)
    {
        if (IsClient)
        {
            GameStatesManager.FragmentSize = packet.Size;
        }
    }
}
