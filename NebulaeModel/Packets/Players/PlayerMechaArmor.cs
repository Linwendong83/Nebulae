namespace NebulaeModel.Packets.Players;

[NebulaeModel.Networking.PacketId(16424587855622946516UL)]
internal class PlayerMechaArmor
{
    public PlayerMechaArmor() { }

    public PlayerMechaArmor(ushort playerId, byte[] appearanceData)
    {
        PlayerId = playerId;
        AppearanceData = appearanceData;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public byte[] AppearanceData { get; set; }
}
