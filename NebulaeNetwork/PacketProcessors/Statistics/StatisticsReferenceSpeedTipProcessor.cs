#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Logger;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Statistics;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Statistics;
internal class StatisticsReferenceSpeedTipProcessor : PacketProcessor<StatisticsReferenceSpeedTipPacket>
{
    protected override void ProcessPacket(StatisticsReferenceSpeedTipPacket packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            try
            {
                using var writer = new BinaryUtils.Writer();
                Multiplayer.Session.Statistics.GetReferenceSpeedTip(writer.BinaryWriter, packet.ItemId, packet.AstroFilter, packet.ItemCycle, packet.ProductionProtoId);
                packet.BinaryData = writer.CloseAndGetBytes();
                conn.SendPacket(packet);
            }
            catch (Exception ex)
            {
                Log.Warn($"StatisticsReferenceSpeedTipPacket request error! {ex}");
            }
        }
        if (IsClient)
        {
            try
            {
                using var reader = new BinaryUtils.Reader(packet.BinaryData);
                Multiplayer.Session.Statistics.LocalReferences.ReceiveTip(packet);
                Multiplayer.Session.Statistics.SetReferenceSpeedTip(reader.BinaryReader, packet.ItemId, packet.AstroFilter, packet.ItemCycle, packet.ProductionProtoId);
            }
            catch (Exception ex)
            {
                Log.Warn($"StatisticsReferenceSpeedTipPacket response error! {ex}");
            }
        }
    }
}
