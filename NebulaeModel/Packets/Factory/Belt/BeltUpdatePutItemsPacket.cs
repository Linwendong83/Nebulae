#region

#endregion

namespace NebulaeModel.Packets.Factory.Belt;

/// <summary>Several items placed on belts in one frame, in a single packet.</summary>
/// <remarks>Batching groups the stack insertions from an inventory drag into one message.</remarks>
[NebulaeModel.Networking.PacketId(6651229384756102938UL)]
internal class BeltUpdatePutItemsPacket
{
    /// <summary>Ceiling on the insertions one packet may carry, above any single drag.</summary>
    public const int MaxEntries = 512;

    public BeltUpdatePutItemsPacket() { }

    public BeltUpdatePutItemsPacket(int planetId, int[] beltId, int[] itemId, byte[] itemCount, byte[] itemInc)
    {
        PlanetId = planetId;
        BeltId = beltId;
        ItemId = itemId;
        ItemCount = itemCount;
        ItemInc = itemInc;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }

    [NebulaeModel.Networking.WireOrder(1)]
    public int[] BeltId { get; set; }

    [NebulaeModel.Networking.WireOrder(2)]
    public int[] ItemId { get; set; }

    [NebulaeModel.Networking.WireOrder(3)]
    public byte[] ItemCount { get; set; }

    [NebulaeModel.Networking.WireOrder(4)]
    public byte[] ItemInc { get; set; }

    /// <summary>
    ///     Every parallel array must hold the same number of insertions, or the packet describes nothing
    ///     the receiver can apply. Checked before anything is written.
    /// </summary>
    public bool IsConsistent()
    {
        return BeltId != null && ItemId != null && ItemCount != null && ItemInc != null &&
               BeltId.Length == ItemId.Length && BeltId.Length == ItemCount.Length &&
               BeltId.Length == ItemInc.Length && BeltId.Length <= MaxEntries;
    }
}
