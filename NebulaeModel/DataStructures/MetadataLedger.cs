using System;
using System.Collections.Generic;
using System.IO;

namespace NebulaeModel.DataStructures;

/// <summary>Durable high-water marks. Online membership is supplied by the session, never saved.</summary>
internal sealed class MetadataLedger
{
    public const int ItemCount = 6;
    public long ClusterKey { get; set; } = long.MinValue;
    public int[] Peak { get; } = new int[ItemCount];
    public Dictionary<string, MetadataAccount> Accounts { get; } = new(StringComparer.Ordinal);

    public MetadataAccount GetAccount(string identity)
    {
        if (string.IsNullOrEmpty(identity)) throw new ArgumentException("Missing player identity", nameof(identity));
        if (!Accounts.TryGetValue(identity, out var account)) Accounts.Add(identity, account = new MetadataAccount());
        return account;
    }

    public bool Advance(int[] production, IEnumerable<string> online)
    {
        if (production == null || production.Length != ItemCount) throw new ArgumentException("Expected six matrices");
        var delta = new int[ItemCount];
        var changed = false;
        for (var i = 0; i < ItemCount; i++)
        {
            var next = Math.Max(Peak[i], Math.Min(2000000000, production[i]));
            delta[i] = next - Peak[i];
            changed |= delta[i] > 0;
            Peak[i] = next;
        }
        if (!changed) return false;
        foreach (var identity in new HashSet<string>(online, StringComparer.Ordinal))
        {
            var account = GetAccount(identity);
            for (var i = 0; i < ItemCount; i++)
                account.Earned[i] = (int)Math.Min(2000000000L, (long)account.Earned[i] + delta[i]);
            account.Sequence++;
        }
        return true;
    }

    public byte[] Export()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(2);
        writer.Write(ClusterKey);
        foreach (var value in Peak) writer.Write(value);
        writer.Write(Accounts.Count);
        foreach (var entry in Accounts)
        {
            writer.Write(entry.Key);
            writer.Write(entry.Value.Sequence);
            writer.Write(entry.Value.Acknowledged);
            foreach (var value in entry.Value.Earned) writer.Write(value);
        }
        return stream.ToArray();
    }

    public static MetadataLedger Import(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        var revision = reader.ReadInt32();
        if (revision is < 1 or > 2) throw new InvalidDataException("Unknown metadata ledger revision");
        var ledger = new MetadataLedger();
        if (revision >= 2) ledger.ClusterKey = reader.ReadInt64();
        for (var i = 0; i < ItemCount; i++) ledger.Peak[i] = ReadAmount(reader);
        var count = reader.ReadInt32();
        if (count < 0 || count > 100000) throw new InvalidDataException("Invalid metadata account count");
        for (var j = 0; j < count; j++)
        {
            var key = reader.ReadString();
            if (ledger.Accounts.ContainsKey(key)) throw new InvalidDataException("Duplicate metadata identity");
            var account = ledger.GetAccount(key);
            account.Sequence = reader.ReadInt64();
            account.Acknowledged = reader.ReadInt64();
            if (account.Sequence < 0 || account.Acknowledged < 0 || account.Acknowledged > account.Sequence)
                throw new InvalidDataException("Invalid metadata sequence");
            for (var i = 0; i < ItemCount; i++) account.Earned[i] = ReadAmount(reader);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing metadata ledger bytes");
        return ledger;
    }

    private static int ReadAmount(BinaryReader reader)
    {
        var value = reader.ReadInt32();
        if (value < 0 || value > 2000000000) throw new InvalidDataException("Invalid metadata amount");
        return value;
    }
}

internal sealed class MetadataAccount
{
    public int[] Earned { get; } = new int[MetadataLedger.ItemCount];
    public long Sequence { get; set; }
    public long Acknowledged { get; set; }
}
