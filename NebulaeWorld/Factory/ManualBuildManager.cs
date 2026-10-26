using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Factory;
using NebulaeWorld.Authority;

namespace NebulaeWorld.Factory;

/// <summary>Manual completion reserves material after its personal checkpoint is accepted.</summary>
internal sealed class ManualBuildManager
{
    private readonly Queue<ManualBuildIntent> requests = new();
    private readonly HashSet<(int Planet, int Id, long Generation)> queued = new();
    private AuthorityEpoch epoch;
    private ConnectionEpoch connection;
    private long pending, checkpointTicket;
    private int reservedItem, reservedCount, reservedInc;
    private AuthorityCommandResultPacket result;
    public bool InventoryPending => pending != 0;
    internal long DiagnosticSequence => pending;
    internal bool DiagnosticWaiting => pending != 0 || requests.Count > 0;
    internal string DiagnosticState => $"command={pending} checkpoint={checkpointTicket} requests={requests.Count} reservedItem={reservedItem} reservedCount={reservedCount} result={result?.ResultCode}";

    private void EnsureWorld()
    {
        var identity = Multiplayer.Session.Authority;
        if (identity.Epoch == epoch && identity.Connection == connection) return;
        epoch = identity.Epoch; connection = identity.Connection;
        requests.Clear(); queued.Clear(); pending = checkpointTicket = 0; result = null;
        reservedItem = reservedCount = reservedInc = 0;
    }
    public void Request(PlanetFactory factory, int id)
    {
        EnsureWorld();
        var generation = Multiplayer.Session.BuildDispatch.Instances.Get(factory, id);
        if (generation <= 0 || requests.Count >= 16 || !queued.Add((factory.planetId, id, generation))) return;
        requests.Enqueue(new ManualBuildIntent(factory.planetId, id, generation, 0, 0));
    }
    public void ReceiveResult(AuthorityCommandResultPacket packet)
    {
        if (pending == 0 || packet.Sequence != pending || result != null ||
            new AuthorityEpoch(packet.EpochHigh, packet.EpochLow) != epoch || new ConnectionEpoch(packet.ConnectionEpoch) != connection) return;
        result = (AuthorityCommandResultPacket)packet.CreateOwnedCopy();
    }
    public void TickClient()
    {
        EnsureWorld();
        if (result == null) return;
        if ((CommandResultCode)result.ResultCode != CommandResultCode.Applied && reservedCount > 0)
        {
            var added = GameMain.mainPlayer.package.AddItemStacked(reservedItem, reservedCount, reservedInc, out var remainInc);
            reservedCount -= added; reservedInc = remainInc;
            if (reservedCount > 0) return;
        }
        if ((CommandResultCode)result.ResultCode == CommandResultCode.Applied && result.ResourceRevision > 0)
            Multiplayer.Session.InventoryTransfers.Applied(result.ResourceRevision);
        result = null; pending = 0; reservedCount = reservedInc = reservedItem = 0;
    }
    public void DispatchPending()
    {
        EnsureWorld();
        var session = Multiplayer.Session;
        if (requests.Count == 0 || session.InventoryTransfers.Pending) return;
        if (!session.Life.EnsureInventoryCheckpoint(ref checkpointTicket)) return;
        checkpointTicket = 0;
        var request = requests.Dequeue(); queued.Remove((request.Planet, request.Prebuild, request.Generation));
        var factory = GameMain.galaxy.PlanetById(request.Planet)?.factory;
        if (session.BuildDispatch.Instances.Get(factory, request.Prebuild) != request.Generation) return;
        var site = factory.prebuildPool[request.Prebuild];
        reservedItem = site.protoId; reservedCount = site.itemRequired; reservedInc = 0;
        if (GameMain.mainPlayer.package.GetItemCount(reservedItem) < reservedCount) return;
        if (reservedCount > 0) GameMain.mainPlayer.package.TakeTailItems(ref reservedItem, ref reservedCount, out reservedInc);
        var intent = new ManualBuildIntent(request.Planet, request.Prebuild, request.Generation, reservedCount, reservedInc);
        if (session.CombatAuthority.SendCommand(default, ManualBuildIntent.Category, intent.Encode(), out pending))
        {
            if (session.WorldPaused) session.Network.SendPacket(new NebulaeModel.Packets.GameStates.GamePauseStepRequest());
        }
        else
        {
            if (reservedCount > 0) GameMain.mainPlayer.package.AddItemStacked(reservedItem, reservedCount, reservedInc, out _);
            pending = 0;
        }
    }

    public CommandOutcome Execute(in QueuedHostCommand command, global::Player actor)
    {
        if (!ManualBuildIntent.TryDecode(command.Packet.Payload, out var intent)) return new CommandOutcome(CommandResultCode.RejectedInvalid);
        var session = Multiplayer.Session;
        var data = session.Server.Players.Get(command.ConnectionPlayerId)?.Data as PlayerData;
        var factory = GameMain.galaxy.PlanetById(intent.Planet)?.factory;
        if (data?.LocalPlanetId != intent.Planet || data.Life.IsAlive != true ||
            session.BuildDispatch.Instances.Get(factory, intent.Prebuild) != intent.Generation ||
            !session.Factories.ContainsPrebuildRequest(intent.Planet, intent.Prebuild)) return new CommandOutcome(CommandResultCode.RejectedTarget);
        var site = factory.prebuildPool[intent.Prebuild];
        var proto = LDB.items.Select(site.protoId);
        var distance = proto?.prefabDesc.selectDistance ?? 35f;
        if ((site.pos - data.LocalPlanetPosition.ToVector3()).sqrMagnitude > distance * distance ||
            factory.HasDestroyedPrebuildUnder(-intent.Prebuild) || factory.HasEnemyBuildingNear(-intent.Prebuild))
            return new CommandOutcome(CommandResultCode.RejectedTarget);
        var package = actor?.package ?? data.Mecha.Inventory;
        if (site.itemRequired != intent.Count || package == null || package.GetItemCount(site.protoId) < intent.Count ||
            !session.InventoryTransfers.CanRecord(command.ConnectionPlayerId, command.Key.Sequence))
            return new CommandOutcome(CommandResultCode.RejectedResource);
        var item = (int)site.protoId; var count = intent.Count; var inc = 0;
        if (count > 0) package.TakeTailItems(ref item, ref count, out inc);
        if (count != intent.Count || inc != intent.Inc)
        {
            if (count > 0) package.AddItemStacked(item, count, inc, out _);
            return new CommandOutcome(CommandResultCode.RejectedResource);
        }
        using (session.Factories.IsIncomingRequest.On())
        using (var context = new FactoryOperationScope())
        {
            session.Factories.EventFactory = factory; session.Factories.TargetPlanet = intent.Planet;
            session.Factories.PacketAuthor = command.ConnectionPlayerId;
            if (GameMain.gpuiManager != null) GameMain.gpuiManager.specifyPlanet = factory.planet;
            factory.BuildFinally(GameMain.mainPlayer, intent.Prebuild, true, true);
            session.Vegetation.CaptureRemote(command.ConnectionPlayerId);
        }
        if (count > 0) session.InventoryTransfers.Record(command.ConnectionPlayerId, command.Key.Sequence, item, -count, inc);
        return new CommandOutcome(CommandResultCode.Applied, GameMain.gameTick, resourceRevision: count > 0 ? command.Key.Sequence : 0);
    }
}
