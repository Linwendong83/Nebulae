#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;

#endregion

namespace NebulaeNetwork.PacketProcessors.Players;
internal class PlayerMechaDIYArmorProcessor : PacketProcessor<PlayerMechaDIYArmor>
{
    protected override void ProcessPacket(PlayerMechaDIYArmor packet, NebulaeConnection conn)
    {
        var appearance = MechaAppearanceCodec.ReadCompressed(packet.DIYAppearanceData);
        try
        {
            // store DIYAppearance and items to serve them when player connects again
            if (IsHost)
            {
                var player = Players.Get(conn);
                if (player == null)
                {
                    return;
                }
                if (player.Data.DIYAppearance == null)
                {
                    player.Data.DIYAppearance = new MechaAppearance();
                    player.Data.DIYAppearance.Init();
                }
                appearance.CopyTo(player.Data.DIYAppearance);

                player.Data.DIYItemId = packet.DIYItemId;
                player.Data.DIYItemValue = packet.DIYItemValue;
            }
            else
            {
                // load DIYAppearance received from host to display in mecha appearance editor
                var player = GameMain.mainPlayer;
                var editor = UIRoot.instance.uiMechaEditor;

                if (player.mecha.diyAppearance == null)
                {
                    player.mecha.diyAppearance = new MechaAppearance();
                    player.mecha.diyAppearance.Init();
                }
                appearance.CopyTo(player.mecha.diyAppearance);

                player.mecha.diyItems.Clear();
                for (var i = 0; i < packet.DIYItemId.Length; i++)
                {
                    player.mecha.diyItems.items[packet.DIYItemId[i]] = packet.DIYItemValue[i];
                }

                if (editor == null)
                {
                    return;
                }
                editor.selection.ClearSelection();
                editor.saveGroup._Close();
                if (editor.mecha.diyAppearance == null)
                {
                    editor.mecha.diyAppearance = new MechaAppearance();
                    editor.mecha.diyAppearance.Init();
                }
                // Modify from UIMechaEditor.ApplyMechaAppearance
                GameMain.mainPlayer.mecha.diyAppearance.CopyTo(editor.mecha.diyAppearance);
                editor.mechaArmorModel.RefreshAllPartObjects();
                editor.mechaArmorModel.RefreshAllBoneObjects();
                editor.mecha.diyAppearance.NotifyAllEvents();
                editor.CalcMechaProperty(); // set mecha.hp and mecha.energyConsumptionCoef
                editor._left_content_height_max = 0f;
                editor.SetLeftScrollTop();
                editor.saveGroup._Open();
            }
        }
        finally { appearance.Free(); }
    }
}
