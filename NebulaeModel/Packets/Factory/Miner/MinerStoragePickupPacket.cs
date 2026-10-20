namespace NebulaeModel.Packets.Factory.Miner;

[NebulaeModel.Networking.PacketId(16654296160088191469UL)]
internal class MinerStoragePickupPacket
{
    public MinerStoragePickupPacket() { }

    public MinerStoragePickupPacket(int minerIndex, int planetId)
    {
        MinerIndex = minerIndex;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int MinerIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PlanetId { get; set; }
}
