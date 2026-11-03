#region

using NebulaeModel.Networking;
using NebulaeWorld;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics.ControlPanel;
using UnityEngine;

#endregion

namespace NebulaeNetwork.PacketProcessors.Logistics.ControlPanel;
internal class LCPAdvancedMinerEntryUpdateProcessor : PacketProcessor<LCPAdvancedMinerEntryUpdate>
{
    static readonly StationStore[] stationStores = new StationStore[1];

    protected override void ProcessPacket(LCPAdvancedMinerEntryUpdate packet, NebulaeConnection conn)
    {
        if (IsHost || !Multiplayer.Session.FacilityDirectory.AcceptRow(packet.Generation)) return;

        UpdateEntry(ControlPanelEntryUpdate.Find<UIControlPanelAdvancedMinerEntry>(packet.Index, packet.AstroId, packet.ObjId), packet);
    }

    private static void UpdateEntry(UIControlPanelAdvancedMinerEntry entry, LCPAdvancedMinerEntryUpdate packet)
    {
        if (entry == null) return;

        // Modify from UIControlPanelAdvancedMinerEntry._OnUpdate
        var isInfiniteResource = GameMain.data.gameDesc.isInfiniteResource;
        var isAllEmpty = packet.VeinCount == packet.EmptyVeinCount;
        entry.warningItemCanvasGroup.alpha = ((packet.LowVeinCount > 0 && !isInfiniteResource) || isAllEmpty) ? 1 : 0;
        entry.warningText.text = (isAllEmpty ? "矿脉全部耗尽警告".Translate() : string.Format("矿脉耗尽警告".Translate(), packet.LowVeinCount));

        var veinProto = LDB.veins.Select(packet.VeinProtoId);
        if (!isAllEmpty && veinProto != null)
        {
            entry.minerItemImage.sprite = veinProto.iconSprite;
            entry.minerItemImage.color = Color.white;
            StringBuilderUtility.WriteKMG(entry.veinAmountSB, 8, packet.TotalVeinAmount, false);
            entry.veinAmountText.text = (isInfiniteResource ? ("(" + packet.VeinCount + ")") : string.Concat(new object[]
            {
                    entry.veinAmountSB.ToString(),
                    "(",
                    packet.VeinCount,
                    ")"
            }));
        }
        else
        {
            entry.minerItemImage.sprite = null;
            entry.minerItemImage.color = Color.clear;
            entry.veinAmountText.text = "";
        }

        // Update item UI
        ref var store = ref stationStores[0];
        store.itemId = packet.ItemId;
        store.count = packet.ItemCount;
        store.localOrder = packet.LocalOrder;
        store.remoteOrder = packet.RemoteOrder;
        store.max = packet.StoreMax;
        store.localLogic = (ELogisticStorage)packet.LocalLogic;
        store.remoteLogic = (ELogisticStorage)packet.RemoteLogic;
        var tmp = entry.station.storage;
        entry.station.storage = stationStores;
        // expand UpdateItems()
        entry.storageItem.SetVisible(stationStores[0].itemId > 0);
        entry.storageItem._Update();
        entry.station.storage = tmp;

        // Update power UI
        entry.powerGroupGo.SetActive(true);
        ControlPanelEntryUpdate.ApplyPower(entry,
            new PowerReadout(entry.powerSignImage, entry.powerCircleBg, entry.powerCircleFg, entry.powerRoundFg,
                entry.powerText, entry.maxChargePowerValue, entry.sbw, entry.sbw2),
            packet.ConsumerRatio, packet.RequirePower, packet.WorkEnergyPerTick, packet.PowerRound);
    }
}
