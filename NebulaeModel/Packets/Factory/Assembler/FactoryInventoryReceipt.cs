using System.Collections.Generic;
using System.IO;
using NebulaeModel.Authority;

namespace NebulaeModel.Packets.Factory.Assembler;

internal static class FactoryInventoryReceipt
{
    public static byte[] Encode(IReadOnlyList<PlayerInventoryTransferLedger.Transfer> transfers)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)transfers.Count);
        foreach (var transfer in transfers) { writer.Write(transfer.Item); writer.Write(transfer.Count); writer.Write(transfer.Inc); writer.Write(transfer.Hand); }
        return stream.ToArray();
    }
    public static bool TryDecode(byte[] data, out List<PlayerInventoryTransferLedger.Transfer> transfers)
    {
        transfers = null;
        if (data == null || data.Length == 0 || data[0] > 32 || data.Length != 1 + data[0] * 13) return false;
        var result = new List<PlayerInventoryTransferLedger.Transfer>(data[0]);
        using var reader = new BinaryReader(new MemoryStream(data, false)); reader.ReadByte();
        for (var i = 0; i < data[0]; i++)
        {
            var item = reader.ReadInt32(); var count = reader.ReadInt32(); var inc = reader.ReadInt32(); var hand = reader.ReadByte();
            if (item <= 0 || count <= 0 || inc < 0 || (long)inc > (long)count * 10 || hand > 1) return false;
            result.Add(new PlayerInventoryTransferLedger.Transfer(item, count, inc, hand == 1));
        }
        transfers = result; return true;
    }
}
