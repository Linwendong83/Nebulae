#region

using NebulaeModel.Networking;
using NebulaeModel;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Session;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Session;
internal class ServerStatusRequestProcessor : PacketProcessor<ServerStatusRequest>
{
    protected override void ProcessPacket(ServerStatusRequest packet, NebulaeConnection conn)
    {
        if (IsClient)
        {
            return;
        }

        // Keep the custom description bounded so a long host entry cannot bloat the probe packet.
        var description = Config.Options?.ServerDescription?.Trim() ?? string.Empty;
        if (description.Length > 200)
        {
            description = description.Substring(0, 200);
        }

        conn.SendPacket(new ServerStatusResponse(Multiplayer.Session.NumPlayers, Multiplayer.Session.IsGameLoaded,
            NebulaeVersion.Normalize(Config.ModVersion), description));
    }
}
