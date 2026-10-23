using System;
using NebulaeModel.Authority;
using NebulaeModel.Networking;

namespace NebulaeModel.Packets.Factory.Assembler;

internal enum FactoryProductMessage : byte { RequestSnapshot, Commit, Snapshot, InputCommit, Recipe, Acceleration, FighterPortsSnapshot }

[PacketId(9723416151934816341UL)]
internal class FactoryProductPacket
{
    [WireOrder(0)] public byte[] Key { get; set; }
    [WireOrder(1)] public byte Message { get; set; }
    [WireOrder(2)] public long Sequence { get; set; }
    [WireOrder(3)] public int Recipe { get; set; }
    [WireOrder(4)] public int Slot { get; set; }
    [WireOrder(5)] public int Item { get; set; }
    [WireOrder(6)] public int Count { get; set; }
    [WireOrder(7)] public int[] Products { get; set; } = Array.Empty<int>();
    [WireOrder(8)] public int[] Counts { get; set; } = Array.Empty<int>();
    [WireOrder(9)] public int Inc { get; set; }
    [WireOrder(10)] public int[] Inputs { get; set; } = Array.Empty<int>();
    [WireOrder(11)] public int[] Served { get; set; } = Array.Empty<int>();
    [WireOrder(12)] public int[] IncServed { get; set; } = Array.Empty<int>();
    [WireOrder(13)] public bool ForceAcceleration { get; set; }
    [WireOrder(14)] public int[] OutputInc { get; set; } = Array.Empty<int>();
    [WireOrder(15)] public int[] Filters { get; set; } = Array.Empty<int>();

    public static byte[] EncodeKey(in ObjectKey key)
    { var bytes = new byte[AuthorityLimits.ObjectKeyBytes]; ObjectKeyCodec.WriteTo(bytes, 0, key); return bytes; }
    public bool TryKey(AuthorityEpoch epoch, out ObjectKey key)
    {
        key = ObjectKeyCodec.ReadRaw(Key, 0);
        return Key?.Length == AuthorityLimits.ObjectKeyBytes && key.IsValid && key.Epoch == epoch && key.Kind == PoolKind.Entity;
    }
    public FactoryProductPacket Copy() => new()
    {
        Key = Key == null ? null : (byte[])Key.Clone(),
        Message = Message,
        Sequence = Sequence,
        Recipe = Recipe,
        Slot = Slot,
        Item = Item,
        Count = Count,
        Products = Products == null ? null : (int[])Products.Clone(),
        Counts = Counts == null ? null : (int[])Counts.Clone(),
        Inc = Inc,
        Inputs = Inputs == null ? null : (int[])Inputs.Clone(),
        Served = Served == null ? null : (int[])Served.Clone(),
        IncServed = IncServed == null ? null : (int[])IncServed.Clone(),
        ForceAcceleration = ForceAcceleration,
        OutputInc = OutputInc == null ? null : (int[])OutputInc.Clone(),
        Filters = Filters == null ? null : (int[])Filters.Clone()
    };
}
