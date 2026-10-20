#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Storage;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Storage;
internal class StorageSyncRequestProcessor : PacketProcessor<StorageSyncRequestPacket>
{
    protected override void ProcessPacket(StorageSyncRequestPacket packet, NebulaeConnection conn)
    {
        if (IsClient)
        {
            return;
        }

        if (GameMain.galaxy.PlanetById(packet.PlanetId) == null)
        {
            return;
        }
        var storageComponent = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.factoryStorage
            ?.storagePool[packet.StorageId];
        if (storageComponent == null)
        {
            return;
        }
        using var writer = new BinaryUtils.Writer();
        storageComponent.Export(writer.BinaryWriter);
        conn.SendPacket(new StorageSyncResponsePacket(packet.PlanetId, packet.StorageId,
            writer.CloseAndGetBytes()) { Sequence = packet.Sequence });
    }
}
