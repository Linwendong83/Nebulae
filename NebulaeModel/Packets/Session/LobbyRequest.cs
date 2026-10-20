namespace NebulaeModel.Packets.Session;

[NebulaeModel.Networking.PacketId(16365204603276758025UL)]
internal class LobbyRequest
{
    public LobbyRequest() { }

    public LobbyRequest(byte[] clientCert, string username)
    {
        Username = username;
        ModVersion = Config.ModVersion;
        ClientCert = clientCert;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public string Username { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public string ModVersion { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public byte[] ClientCert { get; set; }
}
