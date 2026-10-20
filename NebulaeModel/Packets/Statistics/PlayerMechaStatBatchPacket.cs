#region

#endregion

namespace NebulaeModel.Packets.Statistics;

/// <summary>
///     Several mecha production and consumption statistics in one packet.
/// </summary>
/// <remarks>
///     The client reports every <c>AddProductionStat</c> and <c>AddConsumptionStat</c> call separately,
///     and those fire on every crafted item and every mined node, so a short mining trip was dozens of
///     eight-byte packets. Net changes per item are accumulated instead and reported together, which is
///     the same total with one message.
/// </remarks>
[NebulaeModel.Networking.PacketId(4471829305647283910UL)]
internal class PlayerMechaStatBatchPacket
{
    /// <summary>Ceiling on the items one packet may carry, above any item count the player touches.</summary>
    public const int MaxEntries = 512;

    public PlayerMechaStatBatchPacket() { }

    public PlayerMechaStatBatchPacket(int[] itemId, int[] itemCount)
    {
        ItemId = itemId;
        ItemCount = itemCount;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int[] ItemId { get; set; }

    /// <summary>Net change per item, parallel to <see cref="ItemId" />. Negative means consumption.</summary>
    [NebulaeModel.Networking.WireOrder(1)]
    public int[] ItemCount { get; set; }

    /// <summary>
    ///     The arrays must line up, or the packet describes nothing the receiver can apply. Checked
    ///     before anything is written.
    /// </summary>
    public bool IsConsistent()
    {
        return ItemId != null && ItemCount != null && ItemId.Length == ItemCount.Length &&
               ItemId.Length <= MaxEntries;
    }
}
