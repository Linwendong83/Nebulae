namespace NebulaeModel.Packets.Routers;

/// <summary>
///     Client to host envelope that carries already-serialized packets and the star they belong to.
/// </summary>
/// <remarks>
///     A client has no notion of "the local star" on the host, so it hands the host a blob plus a star id
///     and the host fans the blob out to the subscribers of that star. Carrying several blobs lets one
///     user action that touches several packet types pay the envelope overhead once instead of per
///     packet, which matters because a 20-byte packet otherwise spends more on framing than on content.
/// </remarks>
[NebulaeModel.Networking.PacketId(7383152312104589609UL)]
internal class StarBroadcastPacket
{
    /// <summary>Ceiling on the blobs one envelope may carry, so a peer cannot make the host loop.</summary>
    public const int MaxPacketObjects = 256;

    public StarBroadcastPacket() { }

    public StarBroadcastPacket(byte[] packetObject, int starId)
    {
        PacketObjects = [packetObject];
        StarId = starId;
    }

    public StarBroadcastPacket(byte[][] packetObjects, int starId)
    {
        PacketObjects = packetObjects;
        StarId = starId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public byte[][] PacketObjects { get; set; }

    [NebulaeModel.Networking.WireOrder(1)]
    public int StarId { get; set; }
}
