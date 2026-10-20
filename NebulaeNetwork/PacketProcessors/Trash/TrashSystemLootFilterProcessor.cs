#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Trash;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Trash;
internal class TrashSystemLootFilterProcessor : PacketProcessor<TrashSystemLootFilterPacket>
{
    protected override void ProcessPacket(TrashSystemLootFilterPacket packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            Multiplayer.Session.Network.SendPacketExclude(packet, conn);
        }

        using var reader = new BinaryUtils.Reader(packet.EnemyDropBansData);
        GameMain.data.trashSystem.enemyDropBans.Clear();
        var count = reader.BinaryReader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            GameMain.data.trashSystem.enemyDropBans.Add(reader.BinaryReader.ReadInt32());
        }
    }
}
