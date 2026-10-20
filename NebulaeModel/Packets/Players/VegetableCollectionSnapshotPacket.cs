using System;

namespace NebulaeModel.Packets.Players;

[NebulaeModel.Networking.PacketId(4580202799309322245UL)]
internal sealed class VegetableCollectionSnapshotPacket
{
    public VegetableCollectionSnapshotPacket() { }
    public VegetableCollectionSnapshotPacket(byte[] data, bool isAuthoritative = false)
    {
        Data = data;
        IsAuthoritative = isAuthoritative;
    }
    [NebulaeModel.Networking.WireOrder(0)]
    public byte[] Data { get; set; } = Array.Empty<byte>();
    [NebulaeModel.Networking.WireOrder(1)]
    public bool IsAuthoritative { get; set; }
}
