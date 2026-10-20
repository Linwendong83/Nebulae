
#region
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Trash;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Trash;
internal class TrashSystemClearAllTrashProcessor : PacketProcessor<TrashSystemClearAllTrashPacket>
{
    protected override void ProcessPacket(TrashSystemClearAllTrashPacket packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            Server.SendPacketExclude(packet, conn);
        }
        using (Multiplayer.Session.Trashes.IsIncomingRequest.On())
        {
            GameMain.data.trashSystem.ClearAllTrash();
        }
    }
}
