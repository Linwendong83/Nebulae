#region

using System;
using System.Collections.Generic;
using NebulaeModel.Authority;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>
/// Writes host craft facts into the client's ground and sector pools.
/// </summary>
/// <remarks>
/// <para>
/// The game-side <see cref="IReplicaMirrorObserver"/> for <see cref="PoolKind.GroundCraft"/> and
/// <see cref="PoolKind.SpaceCraft"/>. Thin by design: it guards the apply window, tracks which
/// factory instance a ground scope's binding belongs to, and delegates every decision to the pure
/// <see cref="CraftBinding"/> cores. Received facts are installed in the apply window; local
/// motion advances separately at the same frame boundary.
/// </para>
/// <para>
/// A re-imported ground factory is a new pool with new indexes. An instance change replaces the
/// core wholesale — without releasing the old shells into the fresh pool — because the old local
/// slot numbers are lies about it. Sector and craft-pool replacements are tracked the same way.
/// The next baseline or state record installs fresh motion anchors and bindings.
/// </para>
/// <para>
/// Shells carry no fleet/unit/drone logic, so vanilla fleet, targeting, ammo and destruction ticks
/// skip them by construction. Despawn uses the pure removal path; the cores never call a
/// destruction rule that would generate drops, statistics or refunds.
/// </para>
/// </remarks>
internal sealed class CraftReplicaBinding : IReplicaMirrorObserver, IReplicaBaselineReadiness
{
    private sealed class ScopeBinding
    {
        public PlanetFactory Factory;
        public CraftData[] CraftPool;
        public SpaceSector Sector;
        public CraftBinding Core;
        public ICraftPools Pools;
    }

    private readonly Func<bool> applyWindowOpen;
    private readonly Dictionary<(PoolKind, int), ScopeBinding> scopes = [];

    public ClientWorldReplica Replica { get; set; }
    public long RefusalsOutsideApplyWindow { get; private set; }
    public long DeferredBaselines { get; private set; }

    public bool IsReadyForBaseline(ScopeKey scope) => scope.Kind == PoolKind.GroundCraft
        ? FactoryFor(scope.Scope)?.planet.factoryLoaded == true && GameMain.spaceSector?.skillSystem != null
        : scope.Kind != PoolKind.SpaceCraft || GameMain.spaceSector?.skillSystem != null;

    public CraftReplicaBinding(Func<bool> applyWindowOpen = null)
    {
        this.applyWindowOpen = applyWindowOpen ?? ReplicaApplyContext.IsSessionApplyActive;
    }

    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
    {
        if (scope.Kind != PoolKind.GroundCraft && scope.Kind != PoolKind.SpaceCraft) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            BindingDiagnostics.OutsideWindow(nameof(CraftReplicaBinding), scope);
            return;
        }
        var binding = BindingFor(scope);
        if (binding?.Core == null) return;
        if (scope.Kind == PoolKind.GroundCraft && binding.Factory == null) return;
        if (scope.Kind == PoolKind.SpaceCraft && GameMain.spaceSector == null) return;
        binding.Core.ApplyState(key, state);
        BindLocalSlot(binding, key);
    }

    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
    {
        if (scope.Kind != PoolKind.GroundCraft && scope.Kind != PoolKind.SpaceCraft) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            BindingDiagnostics.OutsideWindow(nameof(CraftReplicaBinding), scope);
            return;
        }
        if (scope.Kind == PoolKind.GroundCraft)
        {
            var factory = FactoryFor(scope.Scope);
            if (factory == null)
            {
                DeferredBaselines++;
                return;
            }
            var binding = BindingFor(scope);
            factory.combatGroundSystem.fleets.Reset(); factory.combatGroundSystem.units.Reset();
            for (var id = 1; id < factory.craftCursor; id++)
                if (factory.craftPool[id].prototype != ECraftProto.ConstructionDrone)
                    factory.craftPool[id].fleetId = factory.craftPool[id].unitId = 0;
            binding.Core?.ReconcileBaseline(members);
            foreach (var member in members) BindLocalSlot(binding, member.Key);
            return;
        }
        if (GameMain.spaceSector == null)
        {
            DeferredBaselines++;
            return;
        }
        var space = BindingFor(scope);
        GameMain.spaceSector.combatSpaceSystem.fleets.Reset(); GameMain.spaceSector.combatSpaceSystem.units.Reset();
        for (var id = 1; id < GameMain.spaceSector.craftCursor; id++)
            GameMain.spaceSector.craftPool[id].fleetId = GameMain.spaceSector.craftPool[id].unitId = 0;
        space.Core?.ReconcileBaseline(members);
        foreach (var member in members) BindLocalSlot(space, member.Key);
    }

    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
    {
        if (scope.Kind != PoolKind.GroundCraft && scope.Kind != PoolKind.SpaceCraft) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            BindingDiagnostics.OutsideWindow(nameof(CraftReplicaBinding), scope);
            return;
        }
        if (scopes.TryGetValue((scope.Kind, scope.Scope), out var binding) && binding.Core != null)
        {
            binding.Core.RemoveMember(key);
        }
    }

    private ScopeBinding BindingFor(ScopeKey scope)
    {
        var factory = scope.Kind == PoolKind.GroundCraft ? FactoryFor(scope.Scope) : null;
        var sector = scope.Kind == PoolKind.SpaceCraft ? GameMain.spaceSector : null;
        var pool = factory?.craftPool ?? sector?.craftPool;
        if (!scopes.TryGetValue((scope.Kind, scope.Scope), out var binding))
        {
            binding = new ScopeBinding();
            scopes.Add((scope.Kind, scope.Scope), binding);
        }
        if (binding.Factory != factory || binding.Sector != sector || binding.CraftPool != pool || binding.Core == null && pool != null)
        {
            binding.Factory = factory;
            binding.Sector = sector;
            binding.CraftPool = pool;
            binding.Pools = pool == null ? null : scope.Kind == PoolKind.GroundCraft
                ? new GroundCraftReplicaPools(scope.Scope) : new SpaceCraftReplicaPools();
            binding.Core = binding.Pools != null ? new CraftBinding(scope.Kind, scope.Scope, binding.Pools) : null;
        }
        return binding;
    }
    private void BindLocalSlot(ScopeBinding binding, ObjectKey key)
    {
        if (binding.Pools is ILocalCraftPools slots && slots.TryGetLocalId(key.NativeId, out var localId))
            Replica?.SetLocalComponentBinding(key, localId);
    }

    private static PlanetFactory FactoryFor(int planetId) => GameMain.galaxy?.PlanetById(planetId)?.factory;
}
