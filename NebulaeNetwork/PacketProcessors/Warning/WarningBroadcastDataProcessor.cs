#region

using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Warning;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaeNetwork.PacketProcessors.Warning;
internal class WarningBroadcastDataProcessor : PacketProcessor<WarningBroadcastDataPacket>
{
    protected override void ProcessPacket(WarningBroadcastDataPacket packet, NebulaeConnection conn)
    {
        using (Multiplayer.Session.Warning.IsIncomingBroadcast.On())
        {
            var vocal = (EBroadcastVocal)packet.Vocal;
            var factoryIndex = GameMain.data.galaxy.astrosFactory[packet.AstroId]?.index ?? -1;
            var astroId = packet.AstroId;
            var content = packet.Content;
            var lpos = packet.Lpos.ToVector3();

            if (lpos == Vector3.zero)
            {
                GameMain.data.warningSystem.Broadcast(vocal, factoryIndex, astroId, content);
            }
            else
            {
                GameMain.data.warningSystem.Broadcast(vocal, factoryIndex, astroId, content, lpos);
            }
        }
    }
}
