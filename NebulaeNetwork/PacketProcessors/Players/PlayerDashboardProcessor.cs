#region

using System.IO;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;

#endregion

namespace NebulaeNetwork.PacketProcessors.Players;
internal class PlayerDashboardProcessor : PacketProcessor<PlayerDashboardPacket>
{
    protected override void ProcessPacket(PlayerDashboardPacket packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player != null)
            {
                player.Data.DashboardData = packet.DashboardData;
            }
        }
        else
        {
            if (NebulaeWorld.GameStates.LocalPlayerPreferences.HasDashboard) return;
            if (packet.DashboardData != null && packet.DashboardData.Length > 0 && GameMain.data?.statistics?.charts != null)
            {
                // The dashboard blob is written as plain BinaryWriter data (see UIDashboard_Patch.SyncDashboardToServer
                // and PlanetManager.PreservedDashboardData), so it must be read back without LZ4 decompression.
                using var ms = new MemoryStream(packet.DashboardData);
                using var reader = new BinaryReader(ms);
                GameMain.data.statistics.charts.Import(reader);
                NebulaeWorld.GameStates.LocalPlayerPreferences.SaveDashboard();
                var dashboard = UIRoot.instance?.uiGame?.dashboard;
                if (dashboard != null)
                {
                    dashboard.DetermineCharts();
                    dashboard.UpdateCharts();
                }
            }
        }
    }
}
