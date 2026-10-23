namespace NebulaeModel.Packets.Players;

/// <summary>A player's repair drone launch.</summary>
[NebulaeModel.Networking.PacketId(4522073381834808246UL)]
internal class PlayerEjectMechaDronePacket
{
    public PlayerEjectMechaDronePacket() { }

    public PlayerEjectMechaDronePacket(ushort playerId, int planetId, int targetObjectId, int dronePriority)
    {
        PlayerId = playerId;
        PlanetId = planetId;
        TargetObjectId = targetObjectId;
        DronePriority = dronePriority;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int TargetObjectId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int DronePriority { get; set; }
}
