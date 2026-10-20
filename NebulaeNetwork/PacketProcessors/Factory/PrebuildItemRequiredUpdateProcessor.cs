#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory;
using NebulaeModel.Packets.Players;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory;
internal class PrebuildItemRequiredUpdateProcessor : PacketProcessor<PrebuildItemRequiredUpdate>
{
    protected override void ProcessPacket(PrebuildItemRequiredUpdate packet, NebulaeConnection conn)
    {
        if (packet.PrebuildId <= 0 || packet.ItemCount <= 0) return;
        if (IsHost && Players.Get(conn)?.Data.LocalPlanetId != packet.PlanetId) return;
        var planet = GameMain.galaxy.PlanetById(packet.PlanetId);
        if (planet?.factory == null || packet.PrebuildId >= planet.factory.prebuildCursor)
        {
            return;
        }
        var factory = planet.factory;

        ref var ptr = ref factory.prebuildPool[packet.PrebuildId];
        if (ptr.id == packet.PrebuildId && ptr.itemRequired > 0 && ptr.itemRequired != packet.ItemCount)
            return;
        if (ptr.id != packet.PrebuildId || ptr.itemRequired == 0)
        {
            //Prebuild not exist or the prebuild has satisfied the item requirement (green)
            if (IsHost)
            {
                //Refund the item back to the player
                var itemId = ptr.protoId;
                conn.SendPacket(new PlayerGiveItemPacket(itemId, packet.ItemCount, 0));
            }
            return;
        }

        using (Multiplayer.Session.Factories.IsIncomingRequest.On())
        {
            //From ConstructionModuleComponent.PlaceItems
            ptr.itemRequired = 0;
            factory.NotifyPrebuildChange(ptr.id, 3);
            if (factory.planet.factoryLoaded || factory.planet.factingCompletedStage >= 3)
            {
                factory.AlterPrebuildModelState(ptr.id);
            }
            factory.constructionSystem.AddBuildTargetToModules(ptr.id, ref ptr.pos);
        }
    }
}
