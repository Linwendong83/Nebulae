namespace NebulaeModel.Packets.Universe.Editor;

[NebulaeModel.Networking.PacketId(8924384168051450651UL)]
internal class DysonSphereRemoveLayerPacket
{
    public DysonSphereRemoveLayerPacket() { }

    public DysonSphereRemoveLayerPacket(int starIndex, int layerId)
    {
        StarIndex = starIndex;
        LayerId = layerId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int LayerId { get; set; }
}
