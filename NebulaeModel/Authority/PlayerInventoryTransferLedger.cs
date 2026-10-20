using System.Collections.Generic;

namespace NebulaeModel.Authority;

/// <summary>Signed package transfers not yet included in a personal checkpoint.</summary>
internal sealed class PlayerInventoryTransferLedger
{
    internal readonly struct Transfer
    {
        public Transfer(int item, int count, int inc, bool hand = false, bool research = false) { Item = item; Count = count; Inc = inc; Hand = hand; Research = research; }
        public int Item { get; }
        public int Count { get; }
        public int Inc { get; }
        public bool Hand { get; }
        public bool Research { get; }
    }
    private readonly SortedDictionary<long, Transfer[]> transfers = new();
    public long Acknowledged { get; private set; }
    public long HighestSequence { get; private set; }
    public int PendingCount => transfers.Count;
    public bool CanRecord => transfers.Count < 128;
    public void Record(long sequence, Transfer transfer)
        => Record(sequence, new[] { transfer });
    public void Record(long sequence, IReadOnlyList<Transfer> batch)
    {
        if (sequence <= HighestSequence || batch == null || batch.Count == 0 || batch.Count > 32 || !CanRecord)
            throw new System.InvalidOperationException("Invalid player inventory transfer identity.");
        var copy = new Transfer[batch.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            var transfer = batch[i];
            if (transfer.Item <= 0 || transfer.Count == 0 || transfer.Inc < 0 || transfer.Hand && transfer.Research || transfer.Research && transfer.Inc != 0) throw new System.InvalidOperationException("Invalid player inventory transfer.");
            copy[i] = transfer;
        }
        transfers.Add(sequence, copy); HighestSequence = sequence;
    }
    public bool ValidAcknowledgement(long ack) => ack >= Acknowledged &&
        (ack == Acknowledged || transfers.ContainsKey(ack));
    public IEnumerable<Transfer> PendingAfter(long ack)
    {
        foreach (var pair in transfers) if (pair.Key > ack) foreach (var transfer in pair.Value) yield return transfer;
    }
    public void Acknowledge(long ack)
    {
        if (!ValidAcknowledgement(ack)) throw new System.InvalidOperationException("Invalid player inventory transfer acknowledgement.");
        foreach (var key in new List<long>(transfers.Keys)) if (key <= ack) transfers.Remove(key);
        Acknowledged = ack;
    }
}
