using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Warning;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Warning;

internal class GreenPrebuildWarningRequestProcessor : PacketProcessor<GreenPrebuildWarningRequest>
{
    protected override void ProcessPacket(GreenPrebuildWarningRequest packet, NebulaeConnection conn)
    {
        if (IsHost && Multiplayer.Session.IsGameLoaded)
            Multiplayer.Session.Warning.ChangeGreenPrebuildWarning(packet.PlanetId, packet.Enable);
    }
}

internal class GreenPrebuildWarningStateProcessor : PacketProcessor<GreenPrebuildWarningState>
{
    protected override void ProcessPacket(GreenPrebuildWarningState packet, NebulaeConnection conn)
    {
        if (!IsHost) Multiplayer.Session.Warning.RestoreGreenPrebuildWarningPreferences(packet);
    }
}
