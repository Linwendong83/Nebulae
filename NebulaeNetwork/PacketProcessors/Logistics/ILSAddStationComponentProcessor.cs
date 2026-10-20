#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Logger;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics;
using NebulaeWorld;
using NebulaeWorld.Logistics;

#endregion

namespace NebulaeNetwork.PacketProcessors.Logistics;
internal class ILSAddStationComponentProcessor : PacketProcessor<ILSAddStationComponent>
{
    protected override void ProcessPacket(ILSAddStationComponent packet, NebulaeConnection conn)
    {
        using (Multiplayer.Session.Ships.PatchLockILS.On())
        {
            var galacticTransport = GameMain.data.galacticTransport;
            var stationPool = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.transport.stationPool;
            if (stationPool != null)
            {
                // If we have loaded the factory where the new station was created on, should be able to find
                // it in our PlanetTransport.stationPool
                // Assign gid here so this station will go to galacticTransport.stationPool[gid]
                stationPool[packet.StationId].gid = packet.StationGId;
                if (galacticTransport.AddStationComponent(packet.PlanetId, stationPool[packet.StationId]) != packet.StationGId)
                {
                    Log.Warn(string.Format("AddStationComponent gid mismatch: {0} => {1}".Translate(), stationPool[packet.StationId].gid, packet.StationGId));
                    galacticTransport.stationPool[packet.StationGId] = stationPool[packet.StationId];
                }
                galacticTransport.stationCursor = Math.Max(galacticTransport.stationCursor, packet.StationGId + 1);

                if (stationPool[packet.StationId].entityId != packet.EntityId)
                {
                    Log.Warn(string.Format("Station gid {0} entityId mismatch: {1} => {2}".Translate(), packet.StationGId, stationPool[packet.StationId].entityId, packet.EntityId));
                }
            }
            else
            {
                // If we haven't loaded the factory the new station was create on, we need to create a 
                // "fake" station that we can put into the GalacticTransport.stationPool instead of a real on
                ILSShipManager.CreateFakeStationComponent(packet.StationGId, packet.PlanetId, packet.MaxShipCount);
            }
        }
    }
}
