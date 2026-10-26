using System;
using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Logger;

namespace NebulaeWorld.Player;

/// <summary>One personal inventory watermark for every server-settled resource operation.</summary>
internal sealed class PlayerInventoryTransfers : IDisposable
{
    private readonly Dictionary<ushort, (ConnectionEpoch Connection, PlayerInventoryTransferLedger Ledger)> ledgers = [];
    private AuthorityEpoch epoch;
    private ConnectionEpoch connection;
    private readonly DiagnosticWatchdog diagnosticWaits = new();
    private double nextDiagnosticCheck;
    public void CheckDiagnostics()
    {
        if (Diagnostics.Now < nextDiagnosticCheck) return;
        nextDiagnosticCheck = Diagnostics.Now + 1;
        var session = Multiplayer.Session;
        session.AuthorityRuntime?.CheckDiagnostics();
        if (!session.IsGameLoaded || session.IsDedicated) return;
        diagnosticWaits.Observe("personal-checkpoint", session.Life.BlocksInventory || session.Life.IsExiting,
            () => "[checkpoint] " + session.Life.DiagnosticState, progress: session.Life.DiagnosticSequence);
        diagnosticWaits.Observe("factory-operation", session.FactoryProducts.DiagnosticWaiting,
            () => "[factory-products] " + session.FactoryProducts.DiagnosticState, progress: session.FactoryProducts.DiagnosticSequence);
        diagnosticWaits.Observe("manual-build", session.ManualBuilds.DiagnosticWaiting,
            () => "[build] " + session.ManualBuilds.DiagnosticState, progress: session.ManualBuilds.DiagnosticSequence);
        diagnosticWaits.Observe("research-operation", session.Research.InventoryPending || session.Research.HasUnsettled,
            () => "[research] " + session.Research.DiagnosticState, progress: session.Research.DiagnosticSequence);
        diagnosticWaits.Observe("turret-operation", session.Turrets.DiagnosticWaiting,
            () => "[turret] " + session.Turrets.DiagnosticState, progress: session.Turrets.DiagnosticSequence);
    }
    public long ClientSequence { get; private set; }
    public bool Pending => Multiplayer.Session.BuildDispatch.SupplyPending || Multiplayer.Session.Turrets.InventoryPending || Multiplayer.Session.FactoryProducts.InventoryPending ||
                           Multiplayer.Session.Research.InventoryPending || Multiplayer.Session.ManualBuilds?.InventoryPending == true;

    public void EnsureWorld()
    {
        var identity = Multiplayer.Session.Authority;
        if (epoch == identity.Epoch && connection == identity.Connection) return;
        epoch = identity.Epoch; connection = identity.Connection;
        diagnosticWaits.Clear(); nextDiagnosticCheck = 0;
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
        bool Refuse(string reason, PlayerInventoryTransferLedger.Transfer? transfer = null, int available = -1)
        {
            Diagnostics.Warn("inventory:replay:" + player + ":" + reason,
                () => $"[inventory] checkpoint replay refused player={player} reason={reason} ack={acknowledgement} accepted={ledger.Acknowledged} highest={ledger.HighestSequence} pending={ledger.PendingCount} item={transfer?.Item} count={transfer?.Count} inc={transfer?.Inc} available={available}");
            return false;
        }
        if (!ledger.ValidAcknowledgement(acknowledgement)) return Refuse("invalid-acknowledgement");
        foreach (var transfer in ledger.PendingAfter(acknowledgement))
        {
            var package = checkpoint.Inventory;
            if (transfer.Research)
            {
                if (checkpoint is not MechaData research) return Refuse("missing-research-state", transfer);
                research.ResearchPoints.TryGetValue(transfer.Item, out var points);
                var after = (long)points + transfer.Count;
                if (after < 0 || after > int.MaxValue) return Refuse("research-balance", transfer, points);
                research.ResearchPoints[transfer.Item] = (int)after;
                continue;
            }
            if (transfer.Hand)
            {
                if (checkpoint is not MechaData personal) return Refuse("missing-hand-state", transfer);
                if (transfer.Count < 0)
                {
                    if (personal.TakeHand(transfer.Item, -transfer.Count, out var handInc) != -transfer.Count || handInc != transfer.Inc) return Refuse("hand-debit", transfer);
                }
                else if (!personal.CreditHand(transfer.Item, transfer.Count, transfer.Inc)) return Refuse("hand-credit", transfer);
                continue;
            }
            if (transfer.Count < 0)
            {
                var item = transfer.Item; var count = -transfer.Count;
                if (package.GetItemCount(item) < count) return Refuse("inventory-balance", transfer, package.GetItemCount(item));
                package.TakeTailItems(ref item, ref count, out _);
                if (count != -transfer.Count) return Refuse("inventory-debit", transfer, count);
            }
            else if (package.AddItemStacked(transfer.Item, transfer.Count, transfer.Inc, out _) != transfer.Count) return Refuse("inventory-credit", transfer);
        }
        return true;
    }
    public void Acknowledge(ushort player, long acknowledgement) => Ledger(player).Acknowledge(acknowledgement);
    public void Dispose() { diagnosticWaits.Clear(); ledgers.Clear(); ClientSequence = 0; }
}
