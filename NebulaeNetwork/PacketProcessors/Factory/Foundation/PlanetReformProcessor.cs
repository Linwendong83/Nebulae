#region

using NebulaeModel.Networking;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Foundation;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Foundation;
internal class PlanetReformProcessor : PacketProcessor<PlanetReformPacket>
{
    protected override void ProcessPacket(PlanetReformPacket packet, NebulaeConnection conn)
    {
        var planet = GameMain.galaxy.PlanetById(packet.PlanetId);
        if (planet?.factory == null || packet.CollectionBefore == null || packet.CollectionBefore.Length > VegetableCollectionState.MaxBytes) return;
        ushort author = 0;
        if (IsHost)
        {
            var player = Players.Get(conn, EConnectionStatus.Connected);
            if (player?.Data.LocalPlanetId != packet.PlanetId) return;
            author = player.Id;
            if (!Multiplayer.Session.Vegetation.AcceptCommand(author, packet.CommandSequence)) return;
            packet.CollectionBefore = VegetableCollectionState.Capture(Multiplayer.Session.Vegetation.GetRemote(author));
        }
        var factories = Multiplayer.Session.Factories;
        var previousAuthor = factories.PacketAuthor; var previousPlanet = factories.TargetPlanet;
        var previousSpecify = GameMain.gpuiManager.specifyPlanet;
        if (!IsHost) Multiplayer.Session.Vegetation.BeginReplay(packet.CollectionBefore);
        try
        {
            using (Multiplayer.Session.Planets.IsIncomingRequest.On())
            using (factories.IsIncomingRequest.On())
            {
                factories.PacketAuthor = author; factories.TargetPlanet = packet.PlanetId;
                factories.AddPlanetTimer(packet.PlanetId); GameMain.gpuiManager.specifyPlanet = planet;
                if (packet.IsReform) planet.factory.PlanetReformAll(packet.Type, packet.Color, packet.Bury);
                else planet.factory.PlanetReformRevert();
            }
        }
        finally
        {
            factories.PacketAuthor = previousAuthor; factories.TargetPlanet = previousPlanet;
            GameMain.gpuiManager.specifyPlanet = previousSpecify;
            if (!IsHost) Multiplayer.Session.Vegetation.EndReplay();
        }
        if (IsHost)
        {
            Multiplayer.Session.Server.SendPacketToStarExclude(packet, planet.star.id, conn);
            Multiplayer.Session.Vegetation.CaptureRemote(author);
        }
    }
}
