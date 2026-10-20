#region

using System.Globalization;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Storage;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Storage;
internal class StorageSyncResponseProcessor : PacketProcessor<StorageSyncResponsePacket>
{
    protected override void ProcessPacket(StorageSyncResponsePacket packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            return;
        }
        if (!Multiplayer.Session.Storage.AcceptRefresh(packet.Sequence)) return;

        var storageComponent = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.factoryStorage
            ?.storagePool[packet.StorageIndex];
        if (storageComponent == null)
        {
            return;
        }
        using (var reader = new BinaryUtils.Reader(packet.StorageComponent))
        {
            storageComponent.Import(reader.BinaryReader);
        }
        var storage = Multiplayer.Session.Storage;
        if (!storage.WindowOpened || !ReferenceEquals(storage.ActiveStorageComponent, storageComponent)) return;
        storage.RefreshPending = false;
        var protoId = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory
            ?.entityPool[storageComponent.entityId].protoId;
        if (protoId != null)
        {
            var itemProto = LDB.items.Select((int)protoId);

            //Imitation of UIStorageWindow.OnStorageIdChange()
            Multiplayer.Session.Storage.ActiveWindowTitle.text = itemProto?.name ?? "";
        }
        Multiplayer.Session.Storage.ActiveUIStorageGrid._Free();
        Multiplayer.Session.Storage.ActiveUIStorageGrid._Init(storageComponent);
        Multiplayer.Session.Storage.ActiveStorageComponent = storageComponent;
        Multiplayer.Session.Storage.ActiveUIStorageGrid.SetStorageData(Multiplayer.Session.Storage.ActiveStorageComponent);
        Multiplayer.Session.Storage.ActiveUIStorageGrid._Open();
        Multiplayer.Session.Storage.ActiveUIStorageGrid.OnStorageDataChanged();
        Multiplayer.Session.Storage.ActiveBansSlider.maxValue = storageComponent.size;
        Multiplayer.Session.Storage.ActiveBansSlider.value = storageComponent.size - storageComponent.bans;
        Multiplayer.Session.Storage.ActiveBansValueText.text =
            Multiplayer.Session.Storage.ActiveBansSlider.value.ToString(CultureInfo.InvariantCulture);
        GameMain.galaxy.PlanetById(packet.PlanetId).factory.factoryStorage.storagePool[packet.StorageIndex] =
            storageComponent;
    }
}
