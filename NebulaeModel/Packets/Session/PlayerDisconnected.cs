namespace NebulaeModel.Packets.Session;

[NebulaeModel.Networking.PacketId(12168692520096747034UL)]
internal class PlayerDisconnected
{
    public PlayerDisconnected() { }

    public PlayerDisconnected(ushort playerId, ushort numPlayers)
    {
        PlayerId = playerId;
        NumPlayers = numPlayers;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public ushort NumPlayers { get; set; }
}
