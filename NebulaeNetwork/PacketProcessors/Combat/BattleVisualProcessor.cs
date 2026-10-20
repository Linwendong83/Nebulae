using NebulaeModel.Networking;
using NebulaeModel.DataStructures;
using NebulaeModel.Logger;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Combat;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Combat;

/// <summary>Receives the host's visual-only samples. Clients cannot publish world effects.</summary>
internal class BattleVisualProcessor : PacketProcessor<BattleVisualPacket>
{
    protected override void ProcessPacket(BattleVisualPacket packet, NebulaeConnection conn)
    {
        if (IsHost) return;
        if (packet.StarId <= 0 || packet.Tick < 0 || packet.Sequence <= 0) return;
        try
        {
            var frame = BattleVisualFrame.Import(packet.Data);
            Multiplayer.Session.BattleVisuals.Receive(packet, frame);
        }
        catch (System.Exception error)
        {
            Log.Warn("Invalid host battle visual frame: " + error.Message);
        }
    }
}
