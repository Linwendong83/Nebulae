using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NebulaeModel.Authority;
using NebulaeModel.Logger;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.GameStates;
using NebulaeModel.Packets.Session;

namespace NebulaeWorld.GameStates;

/// <summary>Joining peers own their loading gate; the shared world never waits for them.</summary>
internal sealed class JoinSyncManager : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<INebulaeConnection, JoinSyncBuffer> buffers = new();
    private long bufferedBytes;
    internal long BufferedBytes { get { lock (gate) return bufferedBytes; } }
    private readonly Dictionary<INebulaeConnection, Action> completions = new();
    private JoinSyncView view;
    internal JoinDataTransfer DataTransfer { get; } = new();
    private bool worldDataReceived;
    internal Action CloseLoading { get; set; } = () => UIRoot.instance?.CloseLoadingUI();
    private JoinPacketPolicy policy;
    private JoinPacketPolicy Policy => policy ??= new JoinPacketPolicy();
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public bool LocalPending { get; private set; }
    public bool LocalLoaded { get; private set; }
    public bool CanInteract => !localFailed && (!LocalPending || barrierAcknowledged);
    public bool CanSimulate => LocalLoaded && !localFailed;
    public bool CanSubscribe => !LocalPending || barrier >= 0;
    public long LocalGeneration { get; private set; }
    public long LocalSequence { get; private set; }
    private long barrier = -1;
    private bool barrierAcknowledged;
    private bool finalReceived;
    private bool localFailed;
    private double lastLocalProgress;
    private long installedBaselines;
    private int transferBytes;
    private long localProgress, reportedProgress;
    private double lastProgressReport;
    private NebulaeConnection localConnection;
    private double localStarted;
    private readonly DiagnosticWatchdog watchdog = new();
    public string DiagnosticState => $"connection={localConnection?.Id} generation={LocalGeneration} sequence={LocalSequence} loaded={LocalLoaded} barrier={barrier} acked={barrierAcknowledged} final={finalReceived} transfer={DataTransfer.Stage} planet={DataTransfer.PlanetId} bytes={DataTransfer.ReceivedBytes}/{DataTransfer.TotalBytes} idle={Now - lastLocalProgress:F1}s authorityReady={AuthorityReady} productsReady={Multiplayer.Session?.FactoryProducts?.JoinReady}";
    public bool HasJoiningPeers { get { lock (gate) return buffers.Count != 0; } }

    /// <summary>Unity main thread only. Workers finish the preceding frame and stay parked;
    /// no shared pause flag, tick change, or barrier signal is needed.</summary>
    public static void WaitForSimulation()
    {
        var manager = GameMain.logic?.threadController?.threadManager;
        if (manager?.initialized != true || manager.frameBarrier == null) return;
        var spin = new SpinWait();
        while (manager.frameBarrier.remainingParticipantCount > 1) spin.SpinOnce();
    }

    public void Begin(INebulaeConnection connection, NebulaeModel.DataStructures.PlayerTechBonuses bonuses = null)
    {
        lock (gate)
        {
            if (buffers.ContainsKey(connection)) throw new InvalidOperationException("Duplicate world snapshot request.");
            buffers.Add(connection, new JoinSyncBuffer(connection.Id, Now));
        }
        connection.SendPacket(new JoinSyncBegin { Generation = connection.Id, TechBonuses = bonuses ?? new() });
        Log.Debug($"[join-sync] snapshot begun connection={connection.Id} generation={connection.Id}");
    }

    public void Checkpoint(INebulaeConnection connection, int scope)
    {
        lock (gate)
            if (buffers.TryGetValue(connection, out var buffer))
            {
                var before = buffer.Bytes; buffer.Checkpoint(scope, Now); bufferedBytes += buffer.Bytes - before;
            }
    }

    public void Capture<T>(INebulaeConnection connection, T packet, byte[] bytes) where T : class
    {
        lock (gate)
        {
            if (!buffers.TryGetValue(connection, out var buffer)) return;
            foreach (var entry in Policy.Describe(packet, bytes))
            {
                var limit = Math.Min(JoinSyncBuffer.ConnectionLimit, buffer.Bytes + Math.Max(0, JoinSyncBuffer.ServerLimit - bufferedBytes));
                var before = buffer.Bytes;
                var accepted = buffer.Append(entry.Scope, entry.Data, limit);
                bufferedBytes += buffer.Bytes - before;
                if (!accepted) break;
            }
        }
    }

    public void Loaded(INebulaeConnection connection, long generation, Action complete)
    {
        lock (gate)
        {
            if (!buffers.TryGetValue(connection, out var buffer) || buffer.Generation != generation ||
                buffer.Replaying || buffer.Failed) return;
            buffer.BeginReplay(Now);
            Log.Debug($"[join-sync] replay begun connection={connection.Id} generation={generation} bytes={buffer.Bytes}");
            completions[connection] = complete;
            // Replay may refer to another player's mecha (trash, logistics). Install the roster first.
            connection.SendPacket(new SyncComplete(Multiplayer.Session.Server.Players.GetAllPlayerData(
                Multiplayer.IsDedicated ? null : Multiplayer.Session.LocalPlayer.Data))
            { JoinGeneration = generation });
            SendPending(connection, buffer);
            buffer.SealInitialReplay();
            connection.SendPacket(new JoinSyncBarrier { Generation = generation, Sequence = buffer.SentSequence });
        }
    }

    private static void SendPending(INebulaeConnection connection, JoinSyncBuffer buffer)
    {
        var sequence = buffer.SentSequence + 1;
        foreach (var batch in buffer.Drain())
        {
            connection.SendPacket(new JoinSyncBatch
            {
                Generation = buffer.Generation,
                FirstSequence = sequence,
                Scopes = batch.Select(c => c.Scope).ToArray(),
                Packets = batch.Select(c => c.Data).ToArray()
            });
            sequence += batch.Length;
        }
    }

    public void Acknowledge(INebulaeConnection connection, JoinSyncAck ack)
    {
        WaitForSimulation();
        lock (gate)
        {
            if (!buffers.TryGetValue(connection, out var buffer)) return;
            var before = buffer.Bytes;
            if (!buffer.Acknowledge(ack.Generation, ack.Sequence, Now)) return;
            Log.Debug($"[join-sync] barrier acknowledged connection={connection.Id} generation={ack.Generation} sequence={ack.Sequence} sent={buffer.SentSequence} buffered={buffer.Bytes}");
            bufferedBytes += buffer.Bytes - before;
            // The roster upgrade and tail enqueue are one main-thread transaction. Later
            // broadcasts reach Connected, behind this tail in the same ordered sender.
            SendPending(connection, buffer);
            buffers.Remove(connection);
            bufferedBytes -= buffer.Bytes;
            if (!completions.TryGetValue(connection, out var complete)) return;
            completions.Remove(connection);
            complete();
            connection.SendPacket(new GamePausePacket(Multiplayer.Session.WorldPaused, Multiplayer.Session.WorldPauseStepTick));
            connection.SendPacket(new SimSpeedPacket(Multiplayer.Session.WorldSimSpeed));
            connection.SendPacket(new SyncComplete(Multiplayer.Session.Server.Players.GetAllPlayerData(
                Multiplayer.IsDedicated ? null : Multiplayer.Session.LocalPlayer.Data))
            { JoinGeneration = buffer.Generation, JoinSequence = buffer.SentSequence, IsFinal = true });
        }
    }

    public void Forget(INebulaeConnection connection)
    {
        lock (gate)
        {
            if (buffers.TryGetValue(connection, out var buffer)) { bufferedBytes -= buffer.Bytes; buffers.Remove(connection); }
            completions.Remove(connection);
        }
    }

    public void ReportProgress(INebulaeConnection connection, JoinSyncProgress packet)
    {
        lock (gate)
            if (buffers.TryGetValue(connection, out var buffer)) buffer.ReportProgress(packet.Generation, packet.Progress, Now);
    }

    // A newer authority baseline may name an entity built since the initial replay. Its
    // construction must arrive first, even while this connection is still Syncing.
    public void FlushBeforeAuthority(INebulaeConnection connection)
    {
        lock (gate)
            if (buffers.TryGetValue(connection, out var buffer) && buffer.Replaying && !buffer.Failed)
                SendPending(connection, buffer);
    }

    public void UpdateServer()
    {
        INebulaeConnection[] expired;
        lock (gate) expired = buffers.Where(p => p.Value.Expired(Now)).Select(p => p.Key).ToArray();
        foreach (var connection in expired)
        {
            lock (gate)
                if (buffers.TryGetValue(connection, out var failed))
                    Diagnostics.Warn("join:expired:" + connection.Id, () => $"[join-sync] host rejected joining peer connection={connection.Id} generation={failed.Generation} reason={(failed.Failed ? "buffer-limit" : "no-progress-timeout")} bytes={failed.Bytes}/{JoinSyncBuffer.ConnectionLimit} serverBytes={bufferedBytes}/{JoinSyncBuffer.ServerLimit} replaying={failed.Replaying} sent={failed.SentSequence} required={failed.RequiredSequence} ack={failed.AcknowledgedSequence} idle={Now - failed.LastProgress:F1}s");
            Forget(connection);
            Multiplayer.Session.Server.Disconnect(connection, DisconnectionReason.InvalidData,
                "Joining timed out or exceeded the synchronization buffer. Please reconnect.");
        }
        lock (gate)
            foreach (var pair in buffers)
                if (pair.Value.Replaying) SendPending(pair.Key, pair.Value);
    }

    public void BeginLocal()
    {
        localStarted = Now;
        watchdog.Clear();
        Log.Debug("[join-sync] local join started");
        view?.Dispose();
        DataTransfer.Clear();
        worldDataReceived = false;
        localConnection = null;
        LocalPending = true;
        LocalLoaded = false;
        LocalGeneration = LocalSequence = 0;
        barrier = -1;
        barrierAcknowledged = finalReceived = false;
        localFailed = false;
        installedBaselines = 0;
        transferBytes = 0;
        localProgress = reportedProgress = 0;
        lastProgressReport = 0;
        lastLocalProgress = Now;
    }

    public void Adopt(JoinSyncBegin begin, NebulaeConnection connection)
    {
        localConnection = connection;
        if (!LocalPending || begin.Generation <= 0 || LocalGeneration != 0)
            throw new InvalidOperationException("Unexpected join generation.");
        LocalGeneration = begin.Generation;
        Log.Debug($"[join-sync] adopted generation connection={connection?.Id} generation={LocalGeneration}");
        // Research may finish between the handshake and the world cut. Use the bonuses
        // captured with that cut, then apply subsequent unlocks through the replay stream.
        ((NebulaeModel.DataStructures.MechaData)Multiplayer.Session.LocalPlayer.Data.Mecha).TechBonuses = begin.TechBonuses;
        lastLocalProgress = Now;
    }

    public void MarkLoaded()
    {
        LocalLoaded = true; lastLocalProgress = Now;
        Log.Debug($"[join-sync] local world loaded {DiagnosticState}");
    }

    public void MarkWorldDataReceived() => worldDataReceived = true;

    public void BeginTransfer(JoinDataTransferBegin packet, NebulaeConnection connection)
    {
        // Queued packets from a disposed connection must never affect a newer attempt.
        if (!LocalPending || localFailed || !ReferenceEquals(connection, localConnection)) return;
        DataTransfer.Begin(packet, LocalGeneration);
        Log.Debug($"[join-sync] transfer begun id={packet.TransferId} {DiagnosticState}");
    }

    public void ReceiveTransfer(JoinDataTransferChunk packet, NebulaeConnection connection)
    {
        if (!LocalPending || localFailed || !ReferenceEquals(connection, localConnection)) return;
        var prepared = DataTransfer.Receive(packet);
        ReceivedData(packet.Data.Length);
        if (prepared == null) return;
        Log.Debug($"[join-sync] transferred message index={packet.MessageIndex} bytes={prepared.Length} {DiagnosticState}");
        var data = connection.ReceivePacket(DataTransfer.Decode(prepared));
        if (data == null) throw new InvalidOperationException("Join snapshot message was discarded.");
        ((NebulaeNetPacketProcessor)Multiplayer.Session.Network.PacketProcessor).DispatchInline(data, connection);
    }

    internal JoinLoadingProgress LoadingProgress
    {
        get
        {
            if (DataTransfer.Active || !worldDataReceived)
            {
                var stage = DataTransfer.Stage == JoinDataStage.Factory ? JoinLoadingStage.ReceivingFactory : JoinLoadingStage.ReceivingWorld;
                return new JoinLoadingProgress(stage, DataTransfer.ReceivedBytes, DataTransfer.TotalBytes);
            }
            var planet = GameMain.localPlanet;
            if (planet == null)
                return new JoinLoadingProgress(Multiplayer.Session?.LocalPlayer?.Data?.LocalPlanetId > 0
                    ? JoinLoadingStage.LoadingPlanet : JoinLoadingStage.Preparing);
            if (!planet.loaded) return new JoinLoadingProgress(JoinLoadingStage.LoadingPlanet);
            if (planet.factoryLoaded || planet.factingCompletedStage >= 8) return new JoinLoadingProgress(JoinLoadingStage.Preparing);
            if (planet.factingCompletedStage >= 0 || DataTransfer.Stage == JoinDataStage.Factory && DataTransfer.TotalBytes > 0)
                return new JoinLoadingProgress(JoinLoadingStage.LoadingModels, completedSteps: Math.Max(0, planet.factingCompletedStage));
            return new JoinLoadingProgress(JoinLoadingStage.ReceivingFactory);
        }
    }

    public void RefreshLoadingPrompt(UIGameLoadingSplash splash)
    {
        if (!LocalPending || LocalLoaded || localFailed) return;
        (view ??= new JoinSyncView()).SetPrompt(splash, LoadingProgress.Format(key => key.Translate()));
    }

    public void Apply(JoinSyncBatch batch, NebulaeConnection connection)
    {
        if (!LocalPending || !LocalLoaded || batch.Generation != LocalGeneration ||
            batch.Scopes == null || batch.Packets == null || batch.Scopes.Length != batch.Packets.Length ||
            batch.Packets.Length == 0 || batch.Packets.Length > JoinSyncBuffer.BatchCount ||
            batch.FirstSequence != LocalSequence + 1)
            throw new InvalidOperationException("Invalid join replay sequence.");
        long bytes = 0;
        for (var i = 0; i < batch.Packets.Length; i++)
        {
            var data = batch.Packets[i];
            bytes += data?.Length ?? 0;
            if (bytes > JoinSyncBuffer.ConnectionLimit || !Policy.CanReplay(data, batch.Scopes[i]) ||
                batch.Scopes[i] > 0 && GameMain.galaxy.PlanetById(batch.Scopes[i])?.factory == null)
                throw new InvalidOperationException("Invalid join replay scope.");
        }
        WaitForSimulation();
        foreach (var data in batch.Packets)
        {
            ((NebulaeNetPacketProcessor)Multiplayer.Session.Network.PacketProcessor).DispatchInline(data, connection);
            LocalSequence++;
        }
        ReceivedData((int)bytes);
    }

    public void Barrier(JoinSyncBarrier packet)
    {
        if (!LocalPending || packet.Generation != LocalGeneration || packet.Sequence != LocalSequence)
            throw new InvalidOperationException("Join barrier overtook world changes.");
        barrier = packet.Sequence;
        Log.Debug($"[join-sync] barrier received {DiagnosticState}");
        lastLocalProgress = Now;
    }

    public void Final(SyncComplete packet)
    {
        if (!LocalPending || localFailed) return;
        if (!barrierAcknowledged || packet.JoinGeneration != LocalGeneration || packet.JoinSequence != LocalSequence)
            throw new InvalidOperationException("Join completion overtook world changes.");
        finalReceived = true;
        lastLocalProgress = Now;
    }

    public bool AuthorityReady
    {
        get
        {
            var runtime = Multiplayer.Session?.AuthorityRuntime;
            if (runtime?.WorldReplica == null || runtime.StandingScopes.Count == 0) return false;
            return runtime.StandingScopes.All(scope => runtime.WorldReplica.Versions.TryGetScope(scope, out var state) &&
                state.Phase == SubscriptionPhase.Live);
        }
    }

    public static bool HasReplicaAdapter(PoolKind kind) => kind is PoolKind.Entity or PoolKind.Turret or
        PoolKind.GroundEnemy or PoolKind.GroundCraft or PoolKind.SpaceEnemy or PoolKind.SpaceCraft or
        PoolKind.Base or PoolKind.HiveSummary or PoolKind.Ruin;

    public void UpdateClient()
    {
        if (!LocalPending || localFailed) return;
        watchdog.Observe("join-progress", Now - lastLocalProgress >= 10 && Multiplayer.Session.Goals?.AwaitingSelection != true, () => "[join-sync] " + DiagnosticState, 20);
        var installed = Multiplayer.Session.AuthorityRuntime?.WorldReplica?.SnapshotsInstalledTotal ?? 0;
        if (installed != installedBaselines) { installedBaselines = installed; ReceivedData(1); }
        if (LocalGeneration > 0 && localProgress > reportedProgress && Now - lastProgressReport >= 1)
        {
            Multiplayer.Session.Network.SendPacket(new JoinSyncProgress { Generation = LocalGeneration, Progress = localProgress });
            reportedProgress = localProgress;
            lastProgressReport = Now;
        }
        if (Now - lastLocalProgress >= JoinSyncBuffer.TimeoutSeconds)
        { FailLocal(new TimeoutException("Joining stopped making progress. Please reconnect.")); return; }
        // The goal picker closes the loading splash until its selection starts the world.
        // Keep checking progress and timeouts without covering that required input.
        if (!barrierAcknowledged && Multiplayer.Session.Goals?.AwaitingSelection != true)
        {
            (view ??= new JoinSyncView()).Update(LocalLoaded, LocalLoaded ? null : LoadingProgress.Format(key => key.Translate()));
        }
        if (!LocalLoaded || barrier < 0) return;
        WaitForSimulation();
        var runtime = Multiplayer.Session.AuthorityRuntime;
        runtime.UpdateStandingSubscriptions(GameMain.localPlanet?.id ?? 0, GameMain.localPlanet == null,
            HasReplicaAdapter, GameMain.localStar?.id ?? 0);
        // Running clients catch up by executing native frames. A manually paused world still
        // needs a safe apply window to finish its initial baseline conversation.
        if (Multiplayer.Session.WorldPaused) runtime.ApplyPausedJoin(GameMain.gameTick);
        if (!barrierAcknowledged)
        {
            if (!TryOpenInteraction(AuthorityReady, Multiplayer.Session.FactoryProducts.JoinReady,
                    () => Multiplayer.Session.Network.SendPacket(new JoinSyncAck { Generation = LocalGeneration, Sequence = LocalSequence }))) return;
            view?.Dispose();
            CloseLoading();
            Multiplayer.Session.World.OnAllPlayersSyncCompleted();
            Multiplayer.Session.Life.Publish();
            Multiplayer.Session.World.PublishLocalMovement();
        }
        if (!finalReceived) return;
        LocalPending = false;
        watchdog.Clear();
        Log.Debug($"[join-sync] local join completed elapsed={Now - localStarted:F1}s {DiagnosticState}");
        DataTransfer.Clear();
        Multiplayer.Session.Network.SendPacket(new GameStateRequest());
    }

    // Enqueue the acknowledgement before any UI or simulation can produce a command.
    // The host upgrades this connection while processing that same ordered stream.
    internal bool TryOpenInteraction(bool authorityReady, bool productsReady, Action enqueueAck)
    {
        if (barrierAcknowledged) return CanInteract;
        if (!LocalPending || !LocalLoaded || localFailed || barrier < 0 || !authorityReady || !productsReady) return false;
        enqueueAck();
        barrierAcknowledged = true;
        Log.Debug($"[join-sync] interaction unlocked {DiagnosticState}");
        lastLocalProgress = Now;
        return true;
    }

    public void FailLocal(Exception error)
    {
        if (localFailed) return;
        Log.ErrorWithoutPopup($"[join-sync] joining failed elapsed={Now - localStarted:F1}s {DiagnosticState}", error);
        CancelLocal();
        // Use the transport's ordinary error/disconnect UI, keeping partially loaded data unplayable.
        localConnection?.peerSocket.Close((ushort)DisconnectionReason.InvalidData, "Join synchronization failed. Please reconnect.");
    }

    public void CancelLocal()
    {
        if (!LocalPending) return;
        localFailed = true;
        DataTransfer.Clear();
        view?.Dispose();
        CloseLoading();
    }

    public void TransferProgress(int bytes)
    {
        if (bytes > transferBytes) ReceivedData(bytes - transferBytes);
        transferBytes = bytes;
    }

    public void ReceivedData(int bytes)
    {
        if (!LocalPending || bytes <= 0) return;
        localProgress += bytes;
        lastLocalProgress = Now;
    }

    public void Dispose()
    {
        watchdog.Clear();
        localFailed = true;
        DataTransfer.Clear();
        view?.Dispose();
        lock (gate) { buffers.Clear(); completions.Clear(); bufferedBytes = 0; }
        LocalPending = false;
        localConnection = null;
    }
}
