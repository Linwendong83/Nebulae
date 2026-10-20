#region

using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Players;
internal class PlayerMechaArmorProcessor : PacketProcessor<PlayerMechaArmor>
{
    protected override void ProcessPacket(PlayerMechaArmor packet, NebulaeConnection conn)
    {
        var appearance = MechaAppearanceCodec.ReadCompressed(packet.AppearanceData);
        try
        {
            INebulaePlayer player = null;
            if (IsHost)
            {
                // broadcast to other players
                player = Players.Get(conn);
                if (player != null)
                {
                    Server.SendPacketExclude(packet, conn);
                }
            }

            if (Multiplayer.Session.LocalPlayer.Id == packet.PlayerId)
            {
                appearance.CopyTo(GameMain.mainPlayer.mecha.appearance);
                GameMain.mainPlayer.mechaArmorModel.RefreshAllPartObjects();
                GameMain.mainPlayer.mechaArmorModel.RefreshAllBoneObjects();
                GameMain.mainPlayer.mecha.appearance.NotifyAllEvents();
                GameMain.mainPlayer.mechaArmorModel._Init(GameMain.mainPlayer);
                GameMain.mainPlayer.mechaArmorModel._OnOpen();

                // and also load it in the mecha editor
                var editor = UIRoot.instance.uiMechaEditor;
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
                GameMain.mainPlayer.mecha.appearance.CopyTo(editor.mecha.diyAppearance);
                editor.mechaArmorModel.RefreshAllPartObjects();
                editor.mechaArmorModel.RefreshAllBoneObjects();
                editor.mecha.diyAppearance.NotifyAllEvents();
                editor._left_content_height_max = 0f;
                editor.SetLeftScrollTop();
                editor.saveGroup._Open();
            }
            else
            {
                using (Multiplayer.Session.World.GetRemotePlayersModels(out var remotePlayersModels))
                {
                    if (!remotePlayersModels.TryGetValue(packet.PlayerId, out var playerModel))
                    {
                        return;
                    }
                    appearance.CopyTo(playerModel.MechaInstance.appearance);
                    playerModel.PlayerInstance.mechaArmorModel.RefreshAllPartObjects();
                    playerModel.PlayerInstance.mechaArmorModel.RefreshAllBoneObjects();
                    playerModel.MechaInstance.appearance.NotifyAllEvents();
                    playerModel.PlayerInstance.mechaArmorModel._Init(playerModel.PlayerInstance);
                    playerModel.PlayerInstance.mechaArmorModel._OnOpen();

                    // and store the appearance on the server
                    if (!IsHost || player == null)
                    {
                        return;
                    }
                    if (player.Data.Appearance == null)
                    {
                        player.Data.Appearance = new MechaAppearance();
                        player.Data.Appearance.Init();
                    }
                    playerModel.MechaInstance.appearance.CopyTo(player.Data.Appearance);
                }
            }
        }
        finally { appearance.Free(); }
    }
}
