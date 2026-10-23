using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;

namespace NebulaeWorld.Authority.Adapters;

internal sealed class RuinSnapshotAdapter : IHostWorldView
{
    private readonly AuthorityEpoch epoch;
    private readonly Dictionary<int, SlotGenerationTracker> trackers = new();
    private readonly Dictionary<int, PlanetFactory> factories = new();
    public RuinSnapshotAdapter(AuthorityEpoch epoch) => this.epoch = epoch;
    private SlotGenerationTracker Tracker(int planet)
    {
        if (!trackers.TryGetValue(planet, out var tracker)) trackers[planet] = tracker = new SlotGenerationTracker();
        return tracker;
    }
    public void Retire(int planet, int id) => Tracker(planet).Retire(id);
    public long Generation(int planet, int id)
    {
        var factory = GameMain.galaxy?.PlanetById(planet)?.factory;
        return id > 0 && factory?.ruinPool != null && id < factory.ruinCursor && factory.ruinPool[id].id == id
            ? Tracker(planet).ObserveOccupied(id) : 0;
    }
    public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
    {
        if (scope.Kind != PoolKind.Ruin) return false;
        var factory = GameMain.galaxy?.PlanetById(scope.Scope)?.factory;
        if (factory?.ruinPool == null) return false;
        var tracker = Tracker(scope.Scope);
        if (factories.TryGetValue(scope.Scope, out var previous) && !ReferenceEquals(previous, factory))
            for (var id = 1; id < previous.ruinCursor; id++) tracker.Retire(id);
        factories[scope.Scope] = factory;
        for (var id = 1; id < factory.ruinCursor; id++)
            if (factory.ruinPool[id].id == id)
                members.Add(ObjectKey.Create(epoch, scope.Kind, scope.Scope, id, tracker.ObserveOccupied(id)));
        tracker.EndScan(); return true;
    }
    public bool TryReadState(ObjectKey key, out byte[] state)
    {
        state = null;
        if (key.Kind != PoolKind.Ruin || key.Epoch != epoch || !key.IsValid ||
            Tracker(key.Scope).GenerationOf(key.NativeId) != key.Generation) return false;
        var factory = GameMain.galaxy?.PlanetById(key.Scope)?.factory;
        if (factory?.ruinPool == null || key.NativeId >= factory.ruinCursor || factory.ruinPool[key.NativeId].id != key.NativeId) return false;
        state = RuinStateCodec.Encode(factory.ruinPool[key.NativeId]); return true;
    }
}
