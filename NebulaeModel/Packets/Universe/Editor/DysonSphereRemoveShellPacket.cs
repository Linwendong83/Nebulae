namespace NebulaeModel.Packets.Universe.Editor;

[NebulaeModel.Networking.PacketId(2910973501753302076UL)]
internal class DysonSphereRemoveShellPacket
{
    public DysonSphereRemoveShellPacket() { }

    public DysonSphereRemoveShellPacket(int starIndex, int layerId, int shellId)
    {
        StarIndex = starIndex;
        LayerId = layerId;
        ShellId = shellId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int LayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int ShellId { get; set; }
}
