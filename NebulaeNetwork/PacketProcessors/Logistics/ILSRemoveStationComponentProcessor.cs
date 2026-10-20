#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics;
using NebulaeWorld;

#endregion

/*
 * If client knows the planets factory we call the removal there, if not we call it on the gStationPool if possible
 */
namespace NebulaeNetwork.PacketProcessors.Logistics;
internal class ILSRemoveStationComponentProcessor : PacketProcessor<ILSRemoveStationComponent>
{
    protected override void ProcessPacket(ILSRemoveStationComponent packet, NebulaeConnection conn)
    {
        var pData = GameMain.galaxy.PlanetById(packet.PlanetId);
        if (pData?.factory?.transport != null && packet.StationId < pData.factory.transport.stationPool.Length)
        {
            using (Multiplayer.Session.Ships.PatchLockILS.On())
            {
                pData.factory.transport.RemoveStationComponent(packet.StationId);
            }
        }
        else
        {
            var gStationPool = GameMain.data.galacticTransport.stationPool;
            if (packet.StationGId >= gStationPool.Length)
            {
                return;
            }
            using (Multiplayer.Session.Ships.PatchLockILS.On())
            {
                GameMain.data.galacticTransport.RemoveStationComponent(packet.StationGId);
            }
        }
    }
}
