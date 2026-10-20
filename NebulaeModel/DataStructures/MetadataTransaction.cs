using System;
using System.IO;

namespace NebulaeModel.DataStructures;

internal enum MetadataOperation : byte
{
    BuyTech, Matrix, VariousMatrices, DarkFogItems, IncreaseAggressiveness, DecreaseAggressiveness,
    Truce, WithdrawTruce, Respawn
}

internal enum MetadataTransactionState : byte { Requested, Quoted, DebitPending, Debited, Committed, Rejected, Applied }

internal sealed class MetadataTransaction
{
    public string Id = "";
    public string Owner = "";
    public MetadataOperation Operation;
    public MetadataTransactionState State;
    public int Target;
    public int Count;
    public int Parameter;
    public long Stamp;
    public long Expires;
    public long Sequence;
    public long EffectValue;
    public int[] Cost = new int[6];
    public int[] Before = new int[6];
    public int[] After = new int[6];
    public int[] Items = Array.Empty<int>();
    public int[] Counts = Array.Empty<int>();
    public byte[] BeforePlayer = Array.Empty<byte>();
    public byte[] AfterPlayer = Array.Empty<byte>();
    public string Error = "";

    public byte[] Export() => ExportCore(true);

    public byte[] ExportWire() => ExportCore(false);

    private byte[] ExportCore(bool disk)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        if (disk) w.Write(1);
        w.Write(Id); w.Write(Owner); w.Write((byte)Operation); w.Write((byte)State);
        w.Write(Target); w.Write(Count); w.Write(Parameter); w.Write(Stamp); w.Write(Expires);
        w.Write(Sequence); w.Write(EffectValue);
        for (var i = 0; i < 6; i++) { w.Write(Cost[i]); w.Write(Before[i]); w.Write(After[i]); }
        w.Write(Items.Length);
        for (var i = 0; i < Items.Length; i++) { w.Write(Items[i]); w.Write(Counts[i]); }
        var beforePlayer = BeforePlayer;
        var afterPlayer = AfterPlayer;
        w.Write(beforePlayer.Length); w.Write(beforePlayer);
        w.Write(afterPlayer.Length); w.Write(afterPlayer);
        w.Write(Error);
        return stream.ToArray();
    }

    public static MetadataTransaction Import(byte[] bytes) => ImportCore(bytes, true);

    public static MetadataTransaction ImportWire(byte[] bytes) => ImportCore(bytes, false);

    private static MetadataTransaction ImportCore(byte[] bytes, bool disk)
    {
        using var stream = new MemoryStream(bytes, false);
        using var r = new BinaryReader(stream);
        if (disk && r.ReadInt32() != 1) throw new InvalidDataException("Unknown property transaction version");
        var transaction = new MetadataTransaction
        {
            Id = r.ReadString(),
            Owner = r.ReadString(),
            Operation = (MetadataOperation)r.ReadByte(),
            State = (MetadataTransactionState)r.ReadByte(),
            Target = r.ReadInt32(),
            Count = r.ReadInt32(),
            Parameter = r.ReadInt32(),
            Stamp = r.ReadInt64(),
            Expires = r.ReadInt64(),
            Sequence = r.ReadInt64(),
            EffectValue = r.ReadInt64()
        };
        if (!Guid.TryParseExact(transaction.Id, "N", out _) || transaction.Operation > MetadataOperation.Respawn ||
            transaction.State > MetadataTransactionState.Applied) throw new InvalidDataException("Invalid property transaction");
        for (var i = 0; i < 6; i++)
        {
            transaction.Cost[i] = r.ReadInt32(); transaction.Before[i] = r.ReadInt32(); transaction.After[i] = r.ReadInt32();
            if (transaction.Cost[i] < 0) throw new InvalidDataException("Negative property cost");
        }
        var count = r.ReadInt32();
        if (count < 0 || count > 6) throw new InvalidDataException("Invalid property rewards");
        transaction.Items = new int[count]; transaction.Counts = new int[count];
        for (var i = 0; i < count; i++) { transaction.Items[i] = r.ReadInt32(); transaction.Counts[i] = r.ReadInt32(); }
        transaction.BeforePlayer = ReadBlob(r);
        transaction.AfterPlayer = ReadBlob(r);
        transaction.Error = r.ReadString();
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing property transaction data");
        return transaction;
    }

    private static byte[] ReadBlob(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > 16 * 1024 * 1024) throw new InvalidDataException("Invalid player snapshot size");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return bytes;
    }
}
