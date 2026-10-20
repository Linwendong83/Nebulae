#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Inserter;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Inserter;
internal class NewSetInserterInsertTargetProcessor : PacketProcessor<NewSetInserterInsertTargetPacket>
{
    protected override void ProcessPacket(NewSetInserterInsertTargetPacket packet, NebulaeConnection conn)
    {
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        if (factory == null)
        {
            return;
        }
        Multiplayer.Session.Factories.TargetPlanet = factory.planetId;
        factory.WriteObjectConn(packet.ObjId, 0, true, packet.OtherObjId, -1);

        // setting specifyPlanet here to avoid accessing a null object (see GPUInstancingManager activePlanet getter)
        var pData = GameMain.gpuiManager.specifyPlanet;

        GameMain.gpuiManager.specifyPlanet = GameMain.galaxy.PlanetById(packet.PlanetId);
        factory.factorySystem.SetInserterInsertTarget(packet.InserterId, packet.OtherObjId, packet.Offset);
        GameMain.gpuiManager.specifyPlanet = pData;
        Multiplayer.Session.Factories.TargetPlanet = SessionSentinels.PLANET_NONE;
    }
}
