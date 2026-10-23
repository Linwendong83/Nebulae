namespace NebulaeModel.Packets.GameStates;

internal enum MetadataMessage : byte { Request, Quote, Commit, Result, Query, Cancel, Acknowledge, RespawnCommit }

[NebulaeModel.Networking.PacketId(13344715135070519922UL)]
internal class MetadataOperationPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public string WorldId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public MetadataMessage Message { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public byte[] Transaction { get; set; }
}

[NebulaeModel.Networking.PacketId(15242242861928990275UL)]
internal class PropertyHistoryPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public long Sequence { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int[] Consumption { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public bool BanAchievement { get; set; }
}
