#region

using System;
using System.Collections.Generic;
using NebulaeModel.Authority;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>
/// Writes host combat facts into the client's local pools.
/// </summary>
/// <remarks>
/// <para>
/// This is the game-side <see cref="IReplicaMirrorObserver"/>. It is intentionally thin: it guards
/// the apply window, tracks which factory instance a scope's binding belongs to, repairs the
/// references a factory import leaves dangling, and delegates every decision to the pure
/// <see cref="FactoryCombatBinding"/> core. All writes land inside the replica's apply call, which
/// the session only opens at the frame boundary.
/// </para>
/// <para>
/// The factory-instance tracking exists because a client unloads factories when it leaves a star
/// and re-imports them on return. The re-imported factory is a new pool with new indexes; the old
/// binding core's local stat ids would be lies about it. An instance change therefore replaces the
/// core wholesale — <em>without</em> releasing the old ids into the new pool — and repairs the
/// imported references once, before the mirror reconciles.
/// </para>
/// <para>
/// This binding handles only the <see cref="PoolKind.Entity"/> pool.
/// </para>
/// </remarks>
internal sealed class FactoryCombatReplicaBinding : IReplicaMirrorObserver, IReplicaBaselineReadiness
{
    private sealed class ScopeBinding
    {
        /// <summary>The factory instance the current core was built against, or null until one was seen.</summary>
        public PlanetFactory Factory;
        public EntityData[] EntityPool;

        /// <summary>True once this factory instance's imported references were repaired.</summary>
        public bool ReferencesRepaired;

        public FactoryCombatBinding Core;
    }

    private readonly Func<bool> applyWindowOpen;
    private readonly Dictionary<int, ScopeBinding> scopes = [];

    /// <summary>The replica whose mirror records local component bindings. Set at wiring time.</summary>
    public ClientWorldReplica Replica { get; set; }

    /// <summary>Observer calls refused because no apply window was open. Non-zero means the guard or the wiring broke; fail-closed.</summary>
    public long RefusalsOutsideApplyWindow { get; private set; }

    /// <summary>Baselines that arrived before the local factory existed. They are deferred, not guessed.</summary>
    public long DeferredBaselines { get; private set; }

    public bool IsReadyForBaseline(ScopeKey scope) => scope.Kind != PoolKind.Entity ||
        (FactoryFor(scope.Scope)?.planet.factoryLoaded == true && GameMain.spaceSector?.skillSystem != null);

    /// <param name="applyWindowOpen">
    /// Injected so tests can drive the guard; production passes the live session's apply context.
    /// </param>
    public FactoryCombatReplicaBinding(Func<bool> applyWindowOpen = null)
    {
        this.applyWindowOpen = applyWindowOpen ?? DefaultApplyWindowOpen;
    }

    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
    {
        if (scope.Kind != PoolKind.Entity) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        var binding = BindingFor(scope);
        if (binding == null) return;
        binding.Core.ApplyState(key, state);
    }

    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
    {
        if (scope.Kind != PoolKind.Entity) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        var factory = FactoryFor(scope.Scope);
        if (factory == null)
        {
            // The baseline can install before the client's factory import finishes. Reconciling
            // against a factory we cannot see would defer every record anyway; counting makes the
            // gap visible and the next baseline or state record converges the scope.
            DeferredBaselines++;
            return;
        }

        var binding = BindingFor(scope);
        if (binding.Factory != factory)
        {
            // New pool (first sight, or a re-import after unload): the old core's local ids are
            // meaningless here. Replace it without releasing into the fresh pool, repair the
            // dangling imported references once, then reconcile the baseline.
            binding.Factory = factory;
            binding.ReferencesRepaired = false;
            binding.Core = CreateCore(scope.Scope);
        }
        if (!binding.ReferencesRepaired)
        {
            NeutralizeImportedReferences(factory);
            binding.ReferencesRepaired = true;
        }
        NebulaeWorld.Factory.ConstructionRepairTargets.Invalidate(factory, clear: true);
        binding.Core.ReconcileBaseline(members);
    }

    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
    {
        if (scope.Kind != PoolKind.Entity) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        scopes.TryGetValue(scope.Scope, out var binding);
        binding?.Core.RemoveMember(key);
    }

    private ScopeBinding BindingFor(ScopeKey scope)
    {
        if (!scopes.TryGetValue(scope.Scope, out var binding))
        {
            binding = new ScopeBinding { Core = CreateCore(scope.Scope) };
            scopes.Add(scope.Scope, binding);
        }
        var factory = FactoryFor(scope.Scope);
        if (factory != null && (binding.Factory != factory || binding.EntityPool != factory.entityPool))
        {
            binding.Factory = factory;
            binding.EntityPool = factory.entityPool;
            binding.Core = CreateCore(scope.Scope);
            binding.ReferencesRepaired = false;
        }
        return binding;
    }

    private FactoryCombatBinding CreateCore(int planetId) => new(planetId, new FactoryCombatReplicaPools(planetId),
        (key, localId) => Replica?.SetLocalComponentBinding(key, localId));

    /// <summary>
    /// Clears imported entity, craft, vegetation and vein combat-stat references that name host-local skill
    /// slots. Entities are rebound from the mirror; the enemy pool is untouched.
    /// </summary>
    private static void NeutralizeImportedReferences(PlanetFactory factory)
    {
        NebulaeWorld.Factory.ConstructionRepairTargets.ResetImported(factory);
        for (var i = 1; i < factory.entityCursor; i++)
        {
            if (factory.entityPool[i].id == i && factory.entityPool[i].combatStatId != 0)
            {
                factory.entityPool[i].combatStatId = 0;
            }
        }
        for (var i = 1; i < factory.craftCursor; i++)
        {
            if (factory.craftPool[i].id == i && factory.craftPool[i].combatStatId != 0)
            {
                factory.craftPool[i].combatStatId = 0;
            }
        }
        for (var i = 1; i < factory.vegeCursor; i++)
        {
            if (factory.vegePool[i].id == i && factory.vegePool[i].combatStatId != 0)
            {
                factory.vegePool[i].combatStatId = 0;
            }
        }
        for (var i = 1; i < factory.veinCursor; i++)
        {
            if (factory.veinPool[i].id == i && factory.veinPool[i].combatStatId != 0)
            {
                factory.veinPool[i].combatStatId = 0;
            }
        }
    }

    private static PlanetFactory FactoryFor(int planetId) => GameMain.galaxy?.PlanetById(planetId)?.factory;

    private static bool DefaultApplyWindowOpen()
    {
        var session = Multiplayer.Session;
        return session?.AuthorityRuntime?.ApplyContext?.IsActiveOnCurrentThread ?? false;
    }
}
