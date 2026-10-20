#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Planet;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Planet;
internal class PlanetDetailResponseProcessor : PacketProcessor<PlanetDetailResponse>
{
    protected override void ProcessPacket(PlanetDetailResponse packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            return;
        }


        var planet = Multiplayer.Session.IsInLobby
            ? UIRoot.instance.galaxySelect.starmap._galaxyData.PlanetById(packet.PlanetDataID)
            : GameMain.galaxy.PlanetById(packet.PlanetDataID);
        if (planet == null) return;
        if (!packet.Success) { planet.scanning = false; return; }

        if (packet.VeinCounts.Length > 0)
        {
            if (planet.veinGroups == null || planet.veinGroups.Length != packet.VeinCounts.Length)
            {
                planet.veinGroups = new VeinGroup[packet.VeinCounts.Length];
            }
            for (var i = 1; i < planet.veinGroups.Length; i++)
            {
                planet.veinGroups[i].type = (EVeinType)packet.VeinTypes[i];
                planet.veinGroups[i].count = packet.VeinCounts[i];
                planet.veinGroups[i].amount = packet.VeinAmounts[i];
            }
        }
        planet.landPercent = packet.LandPercent;
        planet.landPercentDirtyFlag = false;

        planet.NotifyScanEnded();
        planet.scanning = false;
        planet.scanned = true;
    }
}
