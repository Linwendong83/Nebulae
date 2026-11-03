namespace NebulaeModel.Packets.Factory;

/// <summary>Many prebuild identities from one build action, so a paste does not cost one frame per entity.</summary>
/// <remarks>
/// <para>
/// <see cref="PrebuildInstancePacket"/> carries one generation, which is right for a single placed
/// building and wrong for a blueprint: the host creates one prebuild per pasted entity in one frame,
/// and each identity used to become its own reliable frame on the ordered send queue. Batches are
/// applied through the same monotonic per-slot map, so chunking and reordering across chunks cannot
/// move a slot backwards.
/// </para>
/// </remarks>
[NebulaeModel.Networking.PacketId(12030011424633737659UL)]
internal sealed class PrebuildInstanceBatchPacket
{
    [NebulaeModel.Networking.WireOrder(0)] public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)] public int[] PrebuildIds { get; set; }
    [NebulaeModel.Networking.WireOrder(2)] public long[] Generations { get; set; }
}
