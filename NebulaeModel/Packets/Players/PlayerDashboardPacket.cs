namespace NebulaeModel.Packets.Players;

[NebulaeModel.Networking.PacketId(5660966412484893321UL)]
internal class PlayerDashboardPacket
{
    public PlayerDashboardPacket() { }

    public PlayerDashboardPacket(ushort playerId, byte[] dashboardData)
    {
        PlayerId = playerId;
        DashboardData = dashboardData;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public byte[] DashboardData { get; set; }
}
