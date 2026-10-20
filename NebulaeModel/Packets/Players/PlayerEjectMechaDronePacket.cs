namespace NebulaeModel.Packets.Players;

[NebulaeModel.Networking.PacketId(4522073381834808246UL)]
internal class PlayerEjectMechaDronePacket
{
    public PlayerEjectMechaDronePacket() { }

    public PlayerEjectMechaDronePacket(ushort playerId, int planetId, int targetObjectId,
        int next1ObjectId, int next2ObjectId, int next3ObjectId, int dronePriority)
    {
        PlayerId = playerId;
        PlanetId = planetId;
        TargetObjectId = targetObjectId;
        Next1ObjectId = next1ObjectId;
        Next2ObjectId = next2ObjectId;
        Next3ObjectId = next3ObjectId;
        DronePriority = dronePriority;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int TargetObjectId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int Next1ObjectId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int Next2ObjectId { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int Next3ObjectId { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int DronePriority { get; set; }
}
