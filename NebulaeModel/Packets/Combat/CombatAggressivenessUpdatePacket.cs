namespace NebulaeModel.Packets.Combat;

[NebulaeModel.Networking.PacketId(2891460876104866170UL)]
internal class CombatAggressivenessUpdatePacket
{
    public CombatAggressivenessUpdatePacket() { }

    public CombatAggressivenessUpdatePacket(ushort playerId, float aggressiveness)
    {
        PlayerId = playerId;
        Aggressiveness = aggressiveness;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public float Aggressiveness { get; set; }
}
