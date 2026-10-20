namespace NebulaeModel.Packets.Combat;

[NebulaeModel.Networking.PacketId(71012055732688748UL)]
internal class CombatTruceUpdatePacket
{
    public CombatTruceUpdatePacket() { }

    public CombatTruceUpdatePacket(ushort playerId, long truceEndTime)
    {
        PlayerId = playerId;
        TruceEndTime = truceEndTime;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public long TruceEndTime { get; set; }
}
