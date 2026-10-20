#region

#endregion

namespace NebulaeModel.Packets.Universe.Editor;

/// <summary>Colour changes on several components of one Dyson sphere layer, in a single packet.</summary>
/// <remarks>One message groups a selection or frame of painting. Colours use four bytes to match the game's
/// <c>Color32</c> representation.</remarks>
[NebulaeModel.Networking.PacketId(7716283945561029384UL)]
internal class DysonSphereColorChangeBatchPacket
{
    /// <summary>Ceiling on the entries one packet may carry, above any realistic selection.</summary>
    public const int MaxEntries = 4096;

    public DysonSphereColorChangeBatchPacket() { }

    public DysonSphereColorChangeBatchPacket(int starIndex, int layerId, byte[] type, int[] index, byte[] rgba)
    {
        StarIndex = starIndex;
        LayerId = layerId;
        Type = type;
        Index = index;
        Rgba = rgba;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }

    [NebulaeModel.Networking.WireOrder(1)]
    public int LayerId { get; set; }

    /// <summary>Component kind per entry, as <see cref="DysonSphereComponentType" />.</summary>
    [NebulaeModel.Networking.WireOrder(2)]
    public byte[] Type { get; set; }

    [NebulaeModel.Networking.WireOrder(3)]
    public int[] Index { get; set; }

    /// <summary>Four bytes per entry, in r/g/b/a order, parallel to <see cref="Index" />.</summary>
    [NebulaeModel.Networking.WireOrder(4)]
    public byte[] Rgba { get; set; }

    /// <summary>
    ///     The arrays must line up and the colour blob must hold four bytes per entry, or the packet
    ///     describes nothing the receiver can apply. Checked before anything is written.
    /// </summary>
    public bool IsConsistent()
    {
        return Type != null && Index != null && Rgba != null &&
               Type.Length == Index.Length && Index.Length > 0 &&
               Rgba.Length == Index.Length * 4 && Index.Length <= MaxEntries;
    }
}
