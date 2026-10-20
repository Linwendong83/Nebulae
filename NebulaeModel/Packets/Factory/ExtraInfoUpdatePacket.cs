namespace NebulaeModel.Packets.Factory;

[NebulaeModel.Networking.PacketId(17678682289266427005UL)]
internal class ExtraInfoUpdatePacket
{
    public ExtraInfoUpdatePacket() { }

    public ExtraInfoUpdatePacket(int planetId, int objId, string info)
    {
        PlanetId = planetId;
        ObjId = objId;
        Info = info;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int ObjId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public string Info { get; set; }
}
