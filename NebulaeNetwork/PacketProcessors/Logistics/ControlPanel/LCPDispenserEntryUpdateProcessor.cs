#region

using NebulaeModel.Networking;
using NebulaeWorld;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics.ControlPanel;
using UnityEngine;

#endregion

namespace NebulaeNetwork.PacketProcessors.Logistics.ControlPanel;
internal class LCPDispenserEntryUpdateProcessor : PacketProcessor<LCPDispenserEntryUpdate>
{
    protected override void ProcessPacket(LCPDispenserEntryUpdate packet, NebulaeConnection conn)
    {
        if (IsHost || !Multiplayer.Session.FacilityDirectory.AcceptRow(packet.Generation)) return;

        UpdateEntry(ControlPanelEntryUpdate.Find<UIControlPanelDispenserEntry>(packet.Index, packet.AstroId, packet.ObjId), packet);
    }

    private static void UpdateEntry(UIControlPanelDispenserEntry entry, LCPDispenserEntryUpdate packet)
    {
        if (entry == null) return;

        // Modify from UIControlPanelDispenserEntry._OnUpdate
        var filter = packet.Filter;
        var playerMode = (EPlayerDeliveryMode)packet.PlayerMode;
        var storageMode = (EStorageDeliveryMode)packet.StorageMode;

        if (playerMode == EPlayerDeliveryMode.Recycle && filter < 0)
        {
            entry.transitItemGroup.alpha = 0f;
            entry.transitItemGroup.blocksRaycasts = false;
            entry.recycleAllText.color = entry.masterWindow.recycleAllColor;
        }
        else
        {
            entry.transitItemGroup.alpha = 1f;
            entry.transitItemGroup.blocksRaycasts = true;
            entry.transitItemText.color = entry.masterWindow.transitItemTextColor;
            entry.recycleAllText.color = Color.clear;
            var itemProto = LDB.items.Select(filter);
            if (itemProto != null)
            {
                entry.transitItemImage.sprite = itemProto.iconSprite;
                entry.transitItemButton.tips.itemId = filter;
                entry.transitItemButton.tips.itemInc = 0;
                entry.transitItemButton.tips.itemCount = 0;
                entry.transitItemButton.tips.type = UIButton.ItemTipType.Other;
                entry.transitItemText.text = packet.ItemCount.ToString();
            }
            else
            {
                entry.transitItemGroup.alpha = 0f;
                entry.transitItemGroup.blocksRaycasts = false;
                entry.recycleAllText.color = Color.clear;
            }
        }
        entry.SetPlayerDeliveryActiveModes(playerMode, entry.mechaDeliverySupply, entry.mechaDeliveryDemand, entry.mechaDeliverySupplyDemand);
        entry.SetStorageDeliveryActiveModes(storageMode, entry.storageDeliverySupply, entry.storageDeliveryDemand);
        entry.courierCountText.text = packet.IdleCourierCount.ToString() + "/" + (packet.IdleCourierCount + packet.WorkCourierCount).ToString();

        // Update power UI
        ControlPanelEntryUpdate.ApplyPower(entry,
            new PowerReadout(entry.powerSignImage, entry.powerCircleBg, entry.powerCircleFg, entry.powerRoundFg,
                entry.powerText, entry.maxChargePowerValue, entry.sbw, entry.sbw2),
            packet.ConsumerRatio, packet.RequirePower, packet.WorkEnergyPerTick, packet.PowerRound);
        entry.warningItemCanvasGroup.alpha = packet.WarningFlag ? 1 : 0;
    }
}
