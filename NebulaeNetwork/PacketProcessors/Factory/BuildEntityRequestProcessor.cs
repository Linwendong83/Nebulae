#region

using NebulaeModel.Networking;
using NebulaeModel.Logger;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory;
using NebulaeWorld;
using NebulaeWorld.Factory;
using NebulaeWorld.Warning;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory;
internal class BuildEntityRequestProcessor : PacketProcessor<BuildEntityRequest>
{
    protected override void ProcessPacket(BuildEntityRequest packet, NebulaeConnection conn)
    {
        if (packet.CollectionBefore == null || packet.CollectionBefore.Length > NebulaeModel.DataStructures.VegetableCollectionState.MaxBytes) return;
        if (!IsHost) Multiplayer.Session.Vegetation.InstallReplicaCollection(packet.AuthorId, packet.CollectionBefore);
        if (IsHost)
        {
            var sender = Players.Get(conn);
            if (sender == null || sender.Id != packet.AuthorId ||
                sender.Data.LocalPlanetId != packet.PlanetId) return;
            if (!Multiplayer.Session.Vegetation.AcceptCommand(sender.Id, packet.CommandSequence)) return;
            if (!GameMain.sandboxToolsEnabled &&
                (!Multiplayer.Session.BuildDispatch.TryGet(packet.PlanetId, packet.PrebuildId, out var claim) ||
                 !claim.Launched || claim.OwnerKind != NebulaeModel.DataStructures.BuildOwnerKind.Player ||
                 claim.OwnerId != sender.Id)) return;
        }
        if (IsHost && !Multiplayer.Session.Factories.ContainsPrebuildRequest(packet.PlanetId, packet.PrebuildId))
        {
            // Prebuild has already been removed, so skip it.
            return;
        }

        var planet = GameMain.galaxy.PlanetById(packet.PlanetId);

        // We only execute the code if the client has loaded the factory at least once.
        // Else it will get it once it goes to the planet for the first time. 
        if (planet?.factory == null || packet.PrebuildId <= 0 || packet.PrebuildId >= planet.factory.prebuildCursor)
        {
            return;
        }
        using (Multiplayer.Session.Factories.IsIncomingRequest.On())
        {
            if (packet.InstanceGeneration <= 0 ||
                Multiplayer.Session.BuildDispatch.Instances.Get(planet.factory, packet.PrebuildId) != packet.InstanceGeneration) return;
            using var context = new FactoryOperationScope();
            Multiplayer.Session.Factories.EventFactory = planet.factory;
            Multiplayer.Session.Factories.TargetPlanet = packet.PlanetId;
            Multiplayer.Session.Factories.PacketAuthor = packet.AuthorId;

            if (packet.EntityId != -1 && packet.EntityId != FactoryManager.GetNextEntityId(planet.factory))
            {
                var warningText =
                    string.Format(
                        "(Desync) EntityId mismatch {0} != {1} on planet {2}. Clients should reconnect!".Translate(),
                        packet.EntityId, FactoryManager.GetNextEntityId(planet.factory), planet.displayName);
                Log.Warn(warningText);
                WarningManager.DisplayTemporaryWarning(warningText, 5000);
                return; // Do not construct under a different id and cascade the mismatch.
            }

            if (planet.factoryLoaded)
            {
                planet.factory.BuildFinally(GameMain.mainPlayer, packet.PrebuildId, true, true);
            }
            else
            {
                Multiplayer.Session.Factories.AddPlanetTimer(packet.PlanetId);
                // setting specifyPlanet here to avoid accessing a null object (see GPUInstancingManager activePlanet getter)
                var pData = GameMain.gpuiManager.specifyPlanet;
                GameMain.gpuiManager.specifyPlanet = GameMain.galaxy.PlanetById(packet.PlanetId);
                // Flatten the terrain for remote planet build by other players
                planet.factory.BuildFinally(GameMain.mainPlayer, packet.PrebuildId, true, true);
                GameMain.gpuiManager.specifyPlanet = pData;
            }

            if (IsHost && packet.AuthorId > 0 && packet.AuthorId <= ushort.MaxValue)
                Multiplayer.Session.Vegetation.CaptureRemote((ushort)packet.AuthorId);

            Multiplayer.Session.Factories.EventFactory = null;
            Multiplayer.Session.Factories.PacketAuthor = SessionSentinels.AUTHOR_NONE;
            Multiplayer.Session.Factories.TargetPlanet = SessionSentinels.PLANET_NONE;
        }
    }
}
