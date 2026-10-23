#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe;

/*
 * Receives change event for name of planet or star and applies the change
 */
internal class NameInputProcessor : PacketProcessor<NameInputPacket>
{
    protected override void ProcessPacket(NameInputPacket packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player != null)
            {
                Server.SendPacketExclude(packet, conn);
            }
        }

        using (Multiplayer.Session.Factories.IsIncomingRequest.On())
        {
            // If in lobby, apply change to UI galaxy
            var galaxyData = Multiplayer.Session.IsInLobby ? UIRoot.instance.galaxySelect.starmap.galaxyData : GameMain.galaxy;
            if (galaxyData == null)
            {
                return;
            }

            for (var i = 0; i < packet.Names.Length; i++)
            {
                if (packet.StarIds[i] != SessionSentinels.STAR_NONE)
                {
                    var star = galaxyData.StarById(packet.StarIds[i]);
                    star.overrideName = packet.Names[i];
                    if (Multiplayer.Session.IsInLobby)
                        star.RegeneratePlanetNames(false, NebulaeModel.Utils.NativeGameAccess.GetGalaxyDescription(UIRoot.instance.galaxySelect));
                    else
                        galaxyData.NotifyAstroNameChange(star.id * 100);
                }
                else
                {
                    var planet = galaxyData.PlanetById(packet.PlanetIds[i]);
                    planet.overrideName = packet.Names[i];
                    galaxyData.NotifyAstroNameChange(planet.id);
                }
            }
            if (Multiplayer.Session.IsInLobby)
            {
                // Refresh star name in lobby
                UIRoot.instance.galaxySelect.starmap.OnGalaxyDataReset();
            }
        }
    }
}
