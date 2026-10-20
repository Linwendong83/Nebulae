namespace NebulaeModel.Packets.Trash;

[NebulaeModel.Networking.PacketId(12567377501680913294UL)]
internal class PersistentDropPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public string OperationId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int Ordinal { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int TrashId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public byte[] Data { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public bool Acknowledgement { get; set; }
}
