namespace NebulaeModel.Packets.Session;

[NebulaeModel.Networking.PacketId(13154225796995520918UL)]
internal class ServerStatusResponse
{
    public ServerStatusResponse() { }

    public ServerStatusResponse(ushort numPlayers, bool isGameLoaded, string nebulaeVersion, string description)
    {
        NumPlayers = numPlayers;
        IsGameLoaded = isGameLoaded;
        NebulaeVersion = nebulaeVersion;
        Description = description;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public ushort NumPlayers { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public bool IsGameLoaded { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public string NebulaeVersion { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public string Description { get; set; }
}
