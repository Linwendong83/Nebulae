namespace NebulaeModel.Packets.Session;

/// <summary>
///     Sent by the server list browser right after opening a connection, before any handshake,
///     to ask the server for the status shown in the list (player count, description, versions).
/// </summary>
[NebulaeModel.Networking.PacketId(997066672688847002UL)]
internal class ServerStatusRequest
{
}
