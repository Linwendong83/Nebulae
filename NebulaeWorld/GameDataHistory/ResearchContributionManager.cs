using System;
using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.GameHistory;
using NebulaeWorld.Authority;

namespace NebulaeWorld.GameDataHistory;

/// <summary>Vanilla mecha research runs locally; only bounded contributions commit on the host.</summary>
internal sealed class ResearchContributionManager : IDisposable
{
    private const int BatchTicks = 30;
    private readonly object gate = new();
    private readonly ResearchContributionBuffer buffer = new();
    private readonly int[] statisticalHashes = new int[BatchTicks];
    private int statisticalCursor;
    private AuthorityEpoch epoch;
    private ConnectionEpoch connection;
    private long pending, lastDispatch;
    private AuthorityCommandResultPacket result;
    private bool needsCheckpoint = true;
    private bool takebackPending;
    private long checkpointTicket;
    public bool HasUnsettled { get { lock (gate) return buffer.Hashes > 0; } }
    public bool InventoryPending => pending != 0;
    internal long DiagnosticSequence => pending;
    internal string DiagnosticState => $"command={pending} checkpoint={checkpointTicket} hashes={buffer.Hashes} needsCheckpoint={needsCheckpoint} takeback={takebackPending} result={result?.ResultCode}";

    private void EnsureWorld()
    {
        var identity = Multiplayer.Session.Authority;
        if (epoch == identity.Epoch && connection == identity.Connection) return;
        lock (gate)
        {
            epoch = identity.Epoch; connection = identity.Connection;
            buffer.Clear(); pending = lastDispatch = 0; result = null; needsCheckpoint = true;
            takebackPending = false; checkpointTicket = 0;
        }
    }
    public bool CanTick(GameHistoryData history)
    {
        EnsureWorld();
        if (!Multiplayer.Session.Authority.IsActive || Multiplayer.Session.Life?.BlocksInventory == true || Multiplayer.Session.Life?.IsExiting == true) return false;
        lock (gate)
            if (takebackPending || buffer.Hashes > 0 && (!history.autoManageLabItems || history.currentTech == 0)) return false;
        var tech = LDB.techs.Select(history.currentTech);
        if (tech == null) return true;
        if (history.techSpeed <= 0) return true;
        var state = history.TechState(history.currentTech);
        lock (gate) return state.hashNeeded - state.hashUploaded > buffer.Outstanding(tech.ID, state.curLevel) &&
            buffer.CanRecord(tech.ID, state.curLevel, history.techSpeed, tech.Items, tech.ItemPoints);
    }
    public void Record(GameHistoryData history, long hashes)
    {
        if (hashes <= 0) return;
        EnsureWorld();
        var tech = LDB.techs.Select(history.currentTech);
        lock (gate) buffer.Record(tech.ID, history.TechState(tech.ID).curLevel, hashes, tech.Items, tech.ItemPoints);
        // AddTechHash is host-owned, but vanilla UI still needs this tick's local research rate.
        history.gameData.statistics.techHashedThisFrame += (int)hashes;
    }

    internal void DeferCommittedStatistics(GameStatData statistics, int hashes)
    {
        // Commands commit before PrepareTick clears the counter. Spread their statistics over the
        // batching interval so the native ten-frame activity window sees continuous research.
        statistics.techHashedThisFrame -= hashes;
        for (var i = 0; i < BatchTicks; i++)
            statisticalHashes[(statisticalCursor + i) % BatchTicks] += hashes / BatchTicks + (i < hashes % BatchTicks ? 1 : 0);
    }

    internal void RecordStatistics(GameStatData statistics)
    {
        statistics.techHashedThisFrame += statisticalHashes[statisticalCursor];
        statisticalHashes[statisticalCursor] = 0;
        statisticalCursor = (statisticalCursor + 1) % BatchTicks;
    }
    public void OverlayCheckpoint(MechaData personal)
    {
        EnsureWorld();
        lock (gate) buffer.Overlay(personal.ResearchPoints);
    }
    public void TakeBackAfterPending()
    {
        EnsureWorld();
        lock (gate)
        {
            if (buffer.Hashes > 0) takebackPending = true;
            else GameMain.mainPlayer.mecha.lab.ManageTakeback();
        }
    }
    public bool BeforeTakeback()
    {
        EnsureWorld();
        lock (gate)
        {
            if (buffer.Hashes == 0) return true;
            takebackPending = true;
            return false;
        }
    }

    public void ReceiveResult(AuthorityCommandResultPacket packet)
    {
        lock (gate)
        {
            if (packet.Sequence != pending || pending == 0 || result != null ||
                new AuthorityEpoch(packet.EpochHigh, packet.EpochLow) != epoch || new ConnectionEpoch(packet.ConnectionEpoch) != connection) return;
            result = (AuthorityCommandResultPacket)packet.CreateOwnedCopy();
        }
    }
    public void TickClient()
    {
        EnsureWorld();
        lock (gate)
        {
            if (result != null)
            {
                var accepted = (CommandResultCode)result.ResultCode == CommandResultCode.Applied ? result.TransactionId : 0;
                foreach (var item in buffer.Complete(accepted)) GameMain.mainPlayer.mecha.lab.itemPoints.Alter(item.Key, item.Value);
                if (accepted > 0 && result.ResourceRevision > 0) Multiplayer.Session.InventoryTransfers.Applied(result.ResourceRevision);
                if ((CommandResultCode)result.ResultCode == CommandResultCode.RejectedResource) needsCheckpoint = true;
                pending = 0; result = null;
            }
            if (takebackPending && buffer.Hashes == 0)
            {
                GameMain.mainPlayer.mecha.lab.ManageTakeback(); takebackPending = false;
            }
        }
    }

    public void DispatchPending()
    {
        EnsureWorld();
        lock (gate)
        {
            var batch = buffer.Peek();
            if (pending != 0 || batch == null || Multiplayer.Session.InventoryTransfers.Pending) return;
            var history = GameMain.history;
            var state = history.TechState(batch.Tech);
            var changed = history.currentTech != batch.Tech || state.curLevel != batch.Level;
            var completing = state.hashNeeded - state.hashUploaded <= buffer.Outstanding(batch.Tech, batch.Level);
            if (!changed && !completing && GameMain.gameTick - lastDispatch < BatchTicks) return;
            if (needsCheckpoint && !Multiplayer.Session.Life.EnsureInventoryCheckpoint(ref checkpointTicket, allowExit: true)) return;
            if (Multiplayer.Session.Life.BlocksInventory) return;
            if (!Multiplayer.Session.CombatAuthority.SendCommand(default, ResearchContributionIntent.Category, batch.Intent.Encode(), out var sequence)) return;
            if (Multiplayer.Session.WorldPaused) Multiplayer.Session.Network.SendPacket(new NebulaeModel.Packets.GameStates.GamePauseStepRequest());
            buffer.Begin(); pending = sequence; lastDispatch = GameMain.gameTick; needsCheckpoint = false; checkpointTicket = 0;
        }
    }

    internal static long AcceptedHashes(in ResearchContributionIntent intent, int currentTech, in TechState state) =>
        currentTech == intent.Tech && state.curLevel == intent.Level && !state.unlocked ? Math.Max(0, Math.Min(intent.Hashes, state.hashNeeded - state.hashUploaded)) : 0;

    internal static bool TryDebit(MechaData personal, int[] items, int[] costs, long hashes, out PlayerInventoryTransferLedger.Transfer[] transfers)
    {
        transfers = null;
        if (personal == null || hashes <= 0 || items == null || costs == null || items.Length != costs.Length || items.Length > 32) return false;
        var debits = new Dictionary<int, int>();
        for (var i = 0; i < items.Length; i++)
        {
            var cost = (long)costs[i] * hashes;
            if (items[i] <= 0 || costs[i] <= 0 || cost > int.MaxValue || debits.ContainsKey(items[i])) return false;
            personal.ResearchPoints.TryGetValue(items[i], out var points);
            if (points < cost) return false;
            debits.Add(items[i], (int)cost);
        }
        transfers = new PlayerInventoryTransferLedger.Transfer[debits.Count]; var index = 0;
        foreach (var debit in debits)
        {
            personal.ResearchPoints[debit.Key] -= debit.Value;
            transfers[index++] = new PlayerInventoryTransferLedger.Transfer(debit.Key, -debit.Value, 0, research: true);
        }
        return true;
    }
    public CommandOutcome Execute(in QueuedHostCommand command)
    {
        if (!ResearchContributionIntent.TryDecode(command.Packet.Payload, out var intent)) return new CommandOutcome(CommandResultCode.RejectedInvalid);
        var session = Multiplayer.Session;
        var personal = session.Server.Players.Get(command.ConnectionPlayerId)?.Data.Mecha as MechaData;
        var history = GameMain.history;
        if (!history.techStates.TryGetValue(intent.Tech, out var state)) return new CommandOutcome(CommandResultCode.RejectedTarget);
        var accepted = AcceptedHashes(intent, history.currentTech, state);
        if (accepted == 0) return new CommandOutcome(CommandResultCode.Applied, GameMain.gameTick);
        var tech = LDB.techs.Select(intent.Tech);
        if (tech == null || !session.InventoryTransfers.CanRecord(command.ConnectionPlayerId, command.Key.Sequence)) return new CommandOutcome(CommandResultCode.RejectedNotReady);
        if (!TryDebit(personal, tech.Items, tech.ItemPoints, accepted, out var transfers)) return new CommandOutcome(CommandResultCode.RejectedResource);
        if (transfers.Length > 0) session.InventoryTransfers.Record(command.ConnectionPlayerId, command.Key.Sequence, transfers);
        var statistics = history.gameData.statistics;
        var previousHashes = statistics.techHashedThisFrame;
        history.AddTechHash(accepted);
        DeferCommittedStatistics(statistics, statistics.techHashedThisFrame - previousHashes);
        return new CommandOutcome(CommandResultCode.Applied, GameMain.gameTick, accepted, transfers.Length > 0 ? command.Key.Sequence : 0);
    }
    public void Dispose() { lock (gate) { buffer.Clear(); result = null; pending = 0; Array.Clear(statisticalHashes, 0, statisticalHashes.Length); } }
}
