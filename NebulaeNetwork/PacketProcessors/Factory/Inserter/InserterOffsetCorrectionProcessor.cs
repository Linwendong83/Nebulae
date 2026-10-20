using NebulaeModel.Packets;
#region

using NebulaeModel.Networking;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Factory.Inserter;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Inserter;
internal class InserterOffsetCorrectionProcessor : PacketProcessor<InserterOffsetCorrectionPacket>
{
    protected override void ProcessPacket(InserterOffsetCorrectionPacket packet, NebulaeConnection conn)
    {
        var pool = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.factorySystem?.inserterPool;
        if (pool == null)
        {
            return;
        }
        Log.Warn(
            $"{packet.PlanetId} Fix inserter{packet.InserterId} pickOffset->{packet.PickOffset} insertOffset->{packet.InsertOffset}");
        pool[packet.InserterId].pickOffset = packet.PickOffset;
        pool[packet.InserterId].insertOffset = packet.InsertOffset;
    }
}
