#region

using NebulaeModel.Networking;
using NebulaeWorld;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics.ControlPanel;

#endregion

namespace NebulaeNetwork.PacketProcessors.Logistics.ControlPanel;
internal class LCPStationEntryUpdateProcessor : PacketProcessor<LCPStationEntryUpdate>
{
    static readonly StationStore[] stationStores = new StationStore[5];

    protected override void ProcessPacket(LCPStationEntryUpdate packet, NebulaeConnection conn)
    {
        if (IsHost || !Multiplayer.Session.FacilityDirectory.AcceptRow(packet.Generation)) return;

        UpdateEntry(ControlPanelEntryUpdate.Find<UIControlPanelStationEntry>(packet.Index, packet.AstroId, packet.ObjId), packet);
    }

    private static void UpdateEntry(UIControlPanelStationEntry entry, LCPStationEntryUpdate packet)
    {
        if (entry == null) return;

        // Modify from UIControlPanelStationEntry._OnUpdate
        // Update item UI
        for (var i = 0; i < 5; i++)
        {
            ref var store = ref stationStores[i];
            store.itemId = packet.ItemId[i];
            store.count = packet.ItemCount[i];
            store.localOrder = packet.LocalOrder[i];
            store.remoteOrder = packet.RemoteOrder[i];
            store.max = packet.StoreMax[i];
            store.localLogic = (ELogisticStorage)packet.LocalLogic[i];
            store.remoteLogic = (ELogisticStorage)packet.RemoteLogic[i];
        }
        var tmp = entry.station.storage;
        entry.station.storage = stationStores;
        // expand entry.UpdateItems()
        entry.storageItem0.SetVisible(stationStores[0].itemId > 0);
        entry.storageItem0._Update();
        entry.storageItem1.SetVisible(stationStores[1].itemId > 0);
        entry.storageItem1._Update();
        entry.storageItem2.SetVisible(stationStores[2].itemId > 0);
        entry.storageItem2._Update();
        entry.storageItem3.SetVisible(stationStores[3].itemId > 0);
        entry.storageItem3._Update();
        entry.storageItem4.SetVisible(stationStores[4].itemId > 0);
        entry.storageItem4._Update();
        entry.station.storage = tmp;

        // Update drone and ship count UI
        switch (entry.target.entryType)
        {
            case EControlPanelEntryType.InterstellarStation:
                entry.SetDeliveryVisible(true);
                entry.droneCountSB.Append(packet.IdleDroneCount);
                entry.droneCountSB.Append('/');
                entry.droneCountSB.Append(packet.IdleDroneCount + packet.WorkDroneCount);
                entry.droneCountText.text = entry.droneCountSB.ToString();
                entry.droneCountSB.Clear();
                entry.shipCountSB.Append(packet.IdleShipCount);
                entry.shipCountSB.Append('/');
                entry.shipCountSB.Append(packet.IdleShipCount + packet.WorkShipCount);
                entry.shipCountText.text = entry.shipCountSB.ToString();
                entry.shipCountSB.Clear();
                entry.warperCountText.text = packet.WarperCount.ToString();
                entry.fillNecessaryButton.button.interactable = true;
                entry.fillNecessaryImage.raycastTarget = true;
                break;

            case EControlPanelEntryType.OrbitCollector:
                entry.SetDeliveryVisible(false);
                entry.droneCountText.text = "";
                entry.shipCountText.text = "";
                entry.warperCountText.text = "";
                entry.fillNecessaryButton.button.interactable = false;
                entry.fillNecessaryImage.raycastTarget = false;
                break;

            case EControlPanelEntryType.VeinCollector:
                entry.SetDeliveryVisible(false);
                entry.droneIconImage.raycastTarget = true;
                entry.droneIconImage.color = entry.masterWindow.deliveryIconColor;
                entry.droneCountText.color = entry.masterWindow.deliveryTextColor;
                entry.droneCountSB.Append(packet.IdleDroneCount);
                entry.droneCountSB.Append('/');
                entry.droneCountSB.Append(packet.IdleDroneCount + packet.WorkDroneCount);
                entry.droneCountText.text = entry.droneCountSB.ToString();
                entry.droneCountSB.Clear();
                entry.shipCountText.text = "";
                entry.warperCountText.text = "";
                entry.fillNecessaryButton.button.interactable = true;
                entry.fillNecessaryImage.raycastTarget = true;
                break;

            case EControlPanelEntryType.LocalStation:
                entry.SetDeliveryVisible(false);
                entry.droneCountText.text = "";
                entry.shipCountText.text = "";
                entry.warperCountText.text = "";
                entry.fillNecessaryButton.button.interactable = false;
                entry.fillNecessaryImage.raycastTarget = false;
                break;
        }

        // Update power UI
        entry.powerGroupGo.SetActive(entry.target.entryType != EControlPanelEntryType.OrbitCollector);
        ControlPanelEntryUpdate.ApplyPower(entry,
            new PowerReadout(entry.powerSignImage, entry.powerCircleBg, entry.powerCircleFg, entry.powerRoundFg,
                entry.powerText, entry.maxChargePowerValue, entry.sbw, entry.sbw2),
            packet.ConsumerRatio, packet.RequirePower, packet.WorkEnergyPerTick, packet.PowerRound);
    }
}
