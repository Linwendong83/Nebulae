using System;
using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;

namespace NebulaeWorld.Authority.Adapters;

internal sealed class RuinReplicaBinding : IReplicaMirrorObserver, IReplicaBaselineReadiness
{
    private readonly Dictionary<(int Planet, int Id), (ObjectKey Key, RuinData[] Pool)> installed = new();
    public bool IsReadyForBaseline(ScopeKey scope) => scope.Kind != PoolKind.Ruin ||
        GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.planet.factoryLoaded == true;
    private static void RequireApply()
    {
        if (Multiplayer.Session?.AuthorityRuntime?.ApplyContext.IsActiveOnCurrentThread != true)
            throw new InvalidOperationException("Ruin outside replica apply.");
    }
    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] bytes)
    {
        if (scope.Kind != PoolKind.Ruin) return;
        RequireApply(); var state = RuinStateCodec.Decode(bytes);
        if (!key.IsValid || key.Kind != scope.Kind || key.Scope != scope.Scope || state.id != key.NativeId)
            throw new InvalidOperationException("Invalid ruin identity.");
        var factory = GameMain.galaxy.PlanetById(scope.Scope).factory;
        var id = key.NativeId;
        if (factory.ruinCapacity <= id) factory.SetRuinCapacity(Math.Max(id + 1, factory.ruinCapacity * 2));
        ref var old = ref factory.ruinPool[id];
        var sameOwner = installed.TryGetValue((scope.Scope, id), out var binding) && binding.Key == key && ReferenceEquals(binding.Pool, factory.ruinPool);
        if (sameOwner && old.id == id && old.modelIndex == state.modelIndex && old.pos == state.pos && old.rot == state.rot)
            old.lifeTime = state.lifeTime;
        else
        {
            if (old.id == id) factory.RemoveRuinWithComponet(id);
            // Reserve the host slot using the native allocator so every hash, platform,
            // collider and display component is created through its normal path.
            for (var index = 0; index < factory.ruinRecycleCursor; index++)
                if (factory.ruinRecycle[index] == id)
                { factory.ruinRecycle[index--] = factory.ruinRecycle[--factory.ruinRecycleCursor]; }
            factory.ruinCursor = Math.Max(factory.ruinCursor, id + 1);
            factory.ruinRecycle[factory.ruinRecycleCursor++] = id;
            factory.AddRuinDataWithComponent(state);
        }
        installed[(scope.Scope, id)] = (key, factory.ruinPool);
        // A construction replay may arrive before this independent ruin stream. Once
        // the host pit is installed, recompute the native generator from its real level.
        if (state.modelIndex == 406 && factory.powerSystem?.genPool != null)
            for (var generatorId = 1; generatorId < factory.powerSystem.genCursor; generatorId++)
            {
                ref var generator = ref factory.powerSystem.genPool[generatorId];
                if (generator.id != generatorId || !generator.geothermal || generator.baseRuinId != id ||
                    generator.entityId <= 0 || generator.entityId >= factory.entityCursor) continue;
                ref var entity = ref factory.entityPool[generator.entityId];
                generator.gthStrength = factory.powerSystem.CalculateGeothermalStrength(entity.pos, entity.rot, id);
            }
    }
    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
    {
        if (scope.Kind != PoolKind.Ruin) return;
        RequireApply(); var keep = new HashSet<int>();
        foreach (var member in members) keep.Add(member.Key.NativeId);
        var factory = GameMain.galaxy.PlanetById(scope.Scope).factory;
        for (var id = 1; id < factory.ruinCursor; id++)
            if (factory.ruinPool[id].id == id && !keep.Contains(id)) factory.RemoveRuinWithComponet(id);
        foreach (var member in members) OnStateApplied(scope, member.Key, member.Revision, member.State);
    }
    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
    {
        if (scope.Kind != PoolKind.Ruin) return;
        RequireApply();
        if (installed.TryGetValue((scope.Scope, key.NativeId), out var binding) && binding.Key != key) return;
        installed.Remove((scope.Scope, key.NativeId));
        var factory = GameMain.galaxy?.PlanetById(scope.Scope)?.factory;
        if (factory?.ruinPool != null && key.NativeId < factory.ruinCursor && factory.ruinPool[key.NativeId].id == key.NativeId)
            factory.RemoveRuinWithComponet(key.NativeId);
    }
}
