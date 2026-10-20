using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Combat;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Combat;
internal class CombatGenerationProcessor : PacketProcessor<CombatGenerationPacket>
{
    protected override void ProcessPacket(CombatGenerationPacket packet, NebulaeConnection conn)
    {
        if (IsClient) Multiplayer.Session.Generations.Import(packet.Data);
    }
}
