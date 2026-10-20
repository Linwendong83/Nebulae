#region

using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Inserter;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Inserter;
internal class NewSetInserterPickTargetProcessor : PacketProcessor<NewSetInserterPickTargetPacket>
{
    protected override void ProcessPacket(NewSetInserterPickTargetPacket packet, NebulaeConnection conn)
    {
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        if (factory == null)
        {
            return;
        }
        Multiplayer.Session.Factories.TargetPlanet = factory.planetId;
        factory.WriteObjectConn(packet.ObjId, 1, false, packet.OtherObjId, -1);

        // setting specifyPlanet here to avoid accessing a null object (see GPUInstancingManager activePlanet getter)
        var pData = GameMain.gpuiManager.specifyPlanet;

        GameMain.gpuiManager.specifyPlanet = GameMain.galaxy.PlanetById(packet.PlanetId);
        factory.factorySystem.SetInserterPickTarget(packet.InserterId, packet.OtherObjId, packet.Offset);
        GameMain.gpuiManager.specifyPlanet = pData;

        factory.entityPool[packet.ObjId].pos = packet.PointPos.ToVector3();
        Multiplayer.Session.Factories.TargetPlanet = SessionSentinels.PLANET_NONE;
    }
}
