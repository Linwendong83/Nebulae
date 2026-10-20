using System.Collections.Generic;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Planet;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Planet;

internal class PlanetDataRequestProcessor : PacketProcessor<PlanetDataRequest>
{
    protected override void ProcessPacket(PlanetDataRequest packet, NebulaeConnection conn)
    {
        if (IsClient || packet.PlanetIDs == null) return;
        var seen = new HashSet<int>();
        foreach (var planetId in packet.PlanetIDs)
        {
            if (!seen.Add(planetId)) continue;
            var planet = GameMain.galaxy.PlanetById(planetId);
            if (planet == null) continue;
            var start = LoadingTimings.Now;
            conn.SendPacket(new PlanetDataResponse(planetId, PlanetRuntimeSnapshot.Export(planet)));
            Multiplayer.Session.Planets.Timings.Record("planet-runtime-export", LoadingTimings.Elapsed(start));
        }
    }
}