using System;
using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;

namespace NebulaeWorld.Player;

/// <summary>One personal inventory watermark for every server-settled resource operation.</summary>
internal sealed class PlayerInventoryTransfers : IDisposable
{
    private readonly Dictionary<ushort, (ConnectionEpoch Connection, PlayerInventoryTransferLedger Ledger)> ledgers = [];
    private AuthorityEpoch epoch;
    private ConnectionEpoch connection;
    public long ClientSequence { get; private set; }
    public bool Pending => Multiplayer.Session.Turrets.InventoryPending || Multiplayer.Session.FactoryProducts.InventoryPending || Multiplayer.Session.Research.InventoryPending;

    public void EnsureWorld()
    {
        var identity = Multiplayer.Session.Authority;
        if (epoch == identity.Epoch && connection == identity.Connection) return;
        epoch = identity.Epoch; connection = identity.Connection;
        ledgers.Clear(); ClientSequence = 0;
    }
    private PlayerInventoryTransferLedger Ledger(ushort player)
    {
        EnsureWorld();
        var current = Multiplayer.Session.AuthorityRuntime.ConnectionEpochFor(player);
        if (!ledgers.TryGetValue(player, out var entry) || entry.Connection != current)
            ledgers[player] = entry = (current, new PlayerInventoryTransferLedger());
        return entry.Ledger;
    }
    public bool CanRecord(ushort player, long sequence) => Ledger(player).CanRecord && sequence > Ledger(player).HighestSequence;
    public void Record(ushort player, long sequence, int item, int count, int inc) =>
        Ledger(player).Record(sequence, new PlayerInventoryTransferLedger.Transfer(item, count, inc));
    public void Record(ushort player, long sequence, IReadOnlyList<PlayerInventoryTransferLedger.Transfer> batch) => Ledger(player).Record(sequence, batch);
    public void Applied(long sequence) { EnsureWorld(); ClientSequence = Math.Max(ClientSequence, sequence); }
    public bool Replay(ushort player, long acknowledgement, IMechaData checkpoint)
    {
        var ledger = Ledger(player);
        if (!ledger.ValidAcknowledgement(acknowledgement)) return false;
        foreach (var transfer in ledger.PendingAfter(acknowledgement))
        {
            var package = checkpoint.Inventory;
            if (transfer.Research)
            {
                if (checkpoint is not MechaData research) return false;
                research.ResearchPoints.TryGetValue(transfer.Item, out var points);
                var after = (long)points + transfer.Count;
                if (after < 0 || after > int.MaxValue) return false;
                research.ResearchPoints[transfer.Item] = (int)after;
                continue;
            }
            if (transfer.Hand)
            {
                if (checkpoint is not MechaData personal) return false;
                if (transfer.Count < 0)
                {
                    if (personal.TakeHand(transfer.Item, -transfer.Count, out var handInc) != -transfer.Count || handInc != transfer.Inc) return false;
                }
                else if (!personal.CreditHand(transfer.Item, transfer.Count, transfer.Inc)) return false;
                continue;
            }
            if (transfer.Count < 0)
            {
                var item = transfer.Item; var count = -transfer.Count;
                if (package.GetItemCount(item) < count) return false;
                package.TakeTailItems(ref item, ref count, out _);
                if (count != -transfer.Count) return false;
            }
            else if (package.AddItemStacked(transfer.Item, transfer.Count, transfer.Inc, out _) != transfer.Count) return false;
        }
        return true;
    }
    public void Acknowledge(ushort player, long acknowledgement) => Ledger(player).Acknowledge(acknowledgement);
    public void Dispose() { ledgers.Clear(); ClientSequence = 0; }
}
