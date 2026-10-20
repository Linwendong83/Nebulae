namespace NebulaeModel.Packets.Factory.PowerTower;

[NebulaeModel.Networking.PacketId(11118901590627516839UL)]
internal class PowerTowerChargerUpdate
{
    public PowerTowerChargerUpdate() { }

    public PowerTowerChargerUpdate(ushort playerId, int planetId, int[] nodeIds)
    {
        PlayerId = playerId;
        PlanetId = planetId;
        NodeIds = nodeIds;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int[] NodeIds { get; set; } = [];
}
