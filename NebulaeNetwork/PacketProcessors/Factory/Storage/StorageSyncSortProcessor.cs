#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Storage;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Storage;
internal class StorageSyncSortProcessor : PacketProcessor<StorageSyncSortPacket>
{
    protected override void ProcessPacket(StorageSyncSortPacket packet, NebulaeConnection conn)
    {
        StorageComponent storage = null;
        var pool = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.factoryStorage?.storagePool;
        if (pool != null && packet.StorageIndex != -1 && packet.StorageIndex < pool.Length)
        {
            storage = pool[packet.StorageIndex];
        }

        if (storage == null)
        {
            return;
        }
        using (Multiplayer.Session.Storage.IsIncomingRequest.On())
        {
            storage.Sort();
        }
    }
}
