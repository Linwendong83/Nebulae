using NebulaeModel.Packets;
#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Factory.Belt;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Belt;
internal class BeltReverseProcessor : PacketProcessor<BeltReverseRequestPacket>
{
    protected override void ProcessPacket(BeltReverseRequestPacket packet, NebulaeConnection conn)
    {
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId).factory;
        if (factory == null)
        {
            return;
        }

        if (IsHost)
        {
            var starId = packet.PlanetId / 100;
            Multiplayer.Session.Server.SendPacketToStar(packet, starId);
        }

        using (Multiplayer.Session.Factories.IsIncomingRequest.On())
        {
            Multiplayer.Session.Factories.EventFactory = factory;
            Multiplayer.Session.Factories.TargetPlanet = packet.PlanetId;
            Multiplayer.Session.Factories.PacketAuthor = packet.AuthorId;
            if (IsHost)
            {
                // Load planet model
                Multiplayer.Session.Factories.AddPlanetTimer(packet.PlanetId);
            }

            var beltWindow = UIRoot.instance.uiGame.beltWindow;
            try
            {
                beltWindow._Close(); // close the window first to avoid changing unrelated variables when setting beltId
                beltWindow.factory = factory;
                beltWindow.traffic = factory.cargoTraffic;
                beltWindow.player = GameMain.mainPlayer;
                beltWindow.beltId = packet.BeltId;
                beltWindow.OnReverseButtonClick(0);
                beltWindow._Close();
            }
            catch (Exception e)
            {
                Log.Warn(e);
                beltWindow._tmp_ids.Clear();
                beltWindow._tmp_cargos.Clear();
                beltWindow._Close();
            }

            Multiplayer.Session.Factories.EventFactory = null;
            Multiplayer.Session.Factories.TargetPlanet = SessionSentinels.PLANET_NONE;
            Multiplayer.Session.Factories.PacketAuthor = SessionSentinels.AUTHOR_NONE;
        }
    }
}
