#region

using System;
using System.Collections.Generic;
using NebulaeModel.Packets.Authority;

#endregion

namespace NebulaeModel.Authority;

/// <summary>
/// The narrow view of a client's local pools that the factory combat binding writes through.
/// </summary>
/// <remarks>
/// <para>
/// The binding's decisions (allocate, update, release, clear a reference) are model logic and run
/// in plain tests; the mechanics of touching vanilla pools are the game adapter's. This interface is
/// the seam, so neither side can leak into the other: the model never sees a game type, and the
/// game adapter never decides <em>whether</em> a write should happen.
/// </para>
/// <para>
/// Every method addresses a client-local slot. Host pool indexes never cross this seam — rebinding
/// host facts onto client-local slots is the entire point of the binding.
/// </para>
/// </remarks>
internal interface IFactoryCombatPools
{
    /// <summary>True when the entity slot exists and is live in the local factory.</summary>
    bool EntityExists(int entityId);

    /// <summary>The entity's current local combat-stat reference, or 0.</summary>
    int ReadEntityCombatStat(int entityId);

    void WriteEntityCombatStat(int entityId, int statId);

    /// <summary>Allocates a client-local combat stat and returns its local id.</summary>
    /// <remarks>The implementation initializes purely local display fields (bar position, size).</remarks>
    int AllocateCombatStat();

    /// <summary>Returns a client-local combat stat to its pool.</summary>
    void ReleaseCombatStat(int statId);

    /// <summary>Writes the host's canonical values into a client-local combat stat.</summary>
    void WriteCombatStat(int statId, in FactoryCombatState state, int planetId, int entityId);

    /// <summary>The entity's current local construct-stat reference, or 0.</summary>
    int ReadEntityConstructStat(int entityId);

    void WriteEntityConstructStat(int entityId, int statId);

    /// <summary>Allocates a client-local construct stat and returns its local id.</summary>
    int AllocateConstructStat();

    /// <summary>Returns a client-local construct stat to its pool.</summary>
    void ReleaseConstructStat(int statId);

    /// <summary>Writes the host's canonical values into a client-local construct stat.</summary>
    void WriteConstructStat(int statId, in FactoryCombatState state, int entityId);
}

/// <summary>
/// Rebuilds and maintains client-local entity → combat/construct references from host facts.
/// </summary>
/// <remarks>
/// <para>
/// The binding is keyed by <see cref="ObjectKey"/> — identity, not slot number. When the host
/// recycles a pool slot, the new object arrives under a new generation and gets fresh local stats;
/// the old key's binding is removed with its key, so a rebuilt building can never inherit the
/// destroyed one's local stat ("满血→再受损→死亡→同 entity ID 重建不串引用").
/// </para>
/// <para>
/// The rules per applied state record:
/// </para>
/// <list type="number">
/// <item>Host says a combat stat exists: allocate a client-local stat on first sight (recording the
/// local id as the mirror's component binding), or update the one already bound. Values are always
/// written absolute from the host record.</item>
/// <item>Host says <c>HasCombatStat=false</c>: the entity's local reference must become 0 and any
/// binding-allocated stat is recycled. This repairs dangling imported references without destroying pool
/// entries the binding does not own.</item>
/// <item>Construct records follow the same two rules symmetrically.</item>
/// </list>
/// <para>
/// Writes only happen through the pool seam, and the whole class runs inside the replica's apply
/// call in production, at the frame boundary.
/// </para>
/// </remarks>
internal sealed class FactoryCombatBinding
{
    private sealed class LocalBinding
    {
        public int CombatStatId;
        public int ConstructStatId;
    }

    private readonly IFactoryCombatPools pools;
    private readonly int planetId;
    private readonly Action<ObjectKey, int> componentBindingRecorded;
    private readonly Dictionary<ObjectKey, LocalBinding> bindings = [];
    private readonly HashSet<ObjectKey> memberScratch = [];

    private long statesApplied;
    private long statesRefused;
    private long statesDeferred;
    private long statsAllocated;
    private long statsReleased;
    private long referencesCleared;
    private long membersRemoved;

    /// <param name="planetId">The planet scope this binding serves.</param>
    /// <param name="pools">The client-local pool seam.</param>
    /// <param name="componentBindingRecorded">
    /// Optional hook so allocated local ids also land in the replica's mirror
    /// (<see cref="ClientWorldReplica.SetLocalComponentBinding"/>).
    /// </param>
    public FactoryCombatBinding(int planetId, IFactoryCombatPools pools,
        Action<ObjectKey, int> componentBindingRecorded = null)
    {
        if (planetId <= 0 || planetId > AuthorityScope.MaxPlanetId)
        {
            throw new ArgumentException("A factory combat binding serves one planet scope.", nameof(planetId));
        }
        this.planetId = planetId;
        this.pools = pools ?? throw new ArgumentNullException(nameof(pools));
        this.componentBindingRecorded = componentBindingRecorded;
    }

    /// <summary>Entity keys this binding currently holds local stats for.</summary>
    public int BoundCount => bindings.Count;

    /// <summary>State records applied into local pools.</summary>
    public long StatesApplied => statesApplied;

    /// <summary>Records refused: undecodable bytes or a key outside this binding's scope.</summary>
    public long StatesRefused => statesRefused;

    /// <summary>
    /// Records deferred because the local factory does not have the entity yet. The baseline may
    /// install before (or long after) the factory import; a deferred record is retried by the next
    /// record for the same key, never guessed.
    /// </summary>
    public long StatesDeferred => statesDeferred;

    public long StatsAllocated => statsAllocated;
    public long StatsReleased => statsReleased;

    /// <summary>Entity references zeroed because the host says no stat exists (or the member left).</summary>
    public long ReferencesCleared => referencesCleared;

    public long MembersRemoved => membersRemoved;

    /// <summary>
    /// Reconciles one applied state record into the local pools.
    /// </summary>
    /// <returns>False when the record was refused or deferred; the caller only counts.</returns>
    public bool ApplyState(in ObjectKey key, byte[] state)
    {
        if (key.Kind != PoolKind.Entity || key.Scope != planetId)
        {
            statesRefused++;
            return false;
        }
        if (state == null || !FactoryCombatStateCodec.TryDecode(state, 0, state.Length, out var combat, out _))
        {
            statesRefused++;
            return false;
        }
        if (!pools.EntityExists(key.NativeId))
        {
            // The entity is not importable yet (factory not loaded, or already dismantled locally).
            // Writing would be a guess about a pool we cannot see; the next record for the key or
            // the next baseline reconciles it.
            statesDeferred++;
            return false;
        }

        if (!bindings.TryGetValue(key, out var binding))
        {
            binding = new LocalBinding();
            bindings[key] = binding;
        }
        ReconcileCombat(key, binding, in combat);
        ReconcileConstruct(key, binding, in combat);
        // A fully unbound entry is bookkeeping noise; drop it so BoundCount says what holds stats.
        if (binding.CombatStatId == 0 && binding.ConstructStatId == 0)
        {
            bindings.Remove(key);
        }
        statesApplied++;
        return true;
    }

    /// <summary>
    /// Releases everything one key holds: the local stats return to their pools and the entity's
    /// references are zeroed, so a recycled slot never inherits them.
    /// </summary>
    public void RemoveMember(in ObjectKey key)
    {
        if (!bindings.TryGetValue(key, out var binding)) return;
        if (binding.CombatStatId != 0)
        {
            if (pools.EntityExists(key.NativeId) && pools.ReadEntityCombatStat(key.NativeId) == binding.CombatStatId)
            {
                pools.WriteEntityCombatStat(key.NativeId, 0);
                referencesCleared++;
            }
            pools.ReleaseCombatStat(binding.CombatStatId);
            statsReleased++;
            binding.CombatStatId = 0;
        }
        if (binding.ConstructStatId != 0)
        {
            if (pools.EntityExists(key.NativeId) &&
                pools.ReadEntityConstructStat(key.NativeId) == binding.ConstructStatId)
            {
                pools.WriteEntityConstructStat(key.NativeId, 0);
                referencesCleared++;
            }
            pools.ReleaseConstructStat(binding.ConstructStatId);
            statsReleased++;
            binding.ConstructStatId = 0;
        }
        bindings.Remove(key);
        membersRemoved++;
    }

    /// <summary>
    /// Reconciles the whole scope against an installed baseline: keys the baseline dropped are
    /// removed, and every shipped state is applied.
    /// </summary>
    /// <remarks>
    /// This is the atomic counterpart of the replica's membership replacement. Eviction goes first,
    /// so an entity whose binding the baseline dropped is clean before any of the new states are
    /// written; a baseline that reuses a slot across generations therefore cannot hand the old
    /// generation's local stat to the new one.
    /// </remarks>
    public void ReconcileBaseline(IReadOnlyList<SnapshotMemberRecord> members)
    {
        memberScratch.Clear();
        foreach (var member in members)
        {
            memberScratch.Add(member.Key);
        }

        List<ObjectKey> doomed = null;
        foreach (var key in bindings.Keys)
        {
            if (!memberScratch.Contains(key))
            {
                doomed ??= new List<ObjectKey>();
                doomed.Add(key);
            }
        }
        if (doomed != null)
        {
            foreach (var key in doomed)
            {
                RemoveMember(key);
            }
        }

        foreach (var member in members)
        {
            if (member.State != null)
            {
                ApplyState(member.Key, member.State);
            }
        }
    }

    private void ReconcileCombat(in ObjectKey key, LocalBinding binding, in FactoryCombatState combat)
    {
        var entityId = key.NativeId;
        if (combat.HasCombatStat)
        {
            if (binding.CombatStatId == 0)
            {
                binding.CombatStatId = pools.AllocateCombatStat();
                statsAllocated++;
                pools.WriteCombatStat(binding.CombatStatId, combat, planetId, entityId);
                pools.WriteEntityCombatStat(entityId, binding.CombatStatId);
                componentBindingRecorded?.Invoke(key, binding.CombatStatId);
            }
            else
            {
                pools.WriteCombatStat(binding.CombatStatId, combat, planetId, entityId);
            }
            return;
        }

        // Host says full health / no record. The local reference must be zero whatever it currently
        // holds — a non-zero value here is either a binding we own (release it) or a dangling import
        // (clear it); neither may survive as a lie about the entity's state.
        var current = pools.ReadEntityCombatStat(entityId);
        if (binding.CombatStatId != 0)
        {
            if (current == binding.CombatStatId)
            {
                pools.WriteEntityCombatStat(entityId, 0);
                referencesCleared++;
            }
            pools.ReleaseCombatStat(binding.CombatStatId);
            statsReleased++;
            binding.CombatStatId = 0;
        }
        else if (current != 0)
        {
            pools.WriteEntityCombatStat(entityId, 0);
            referencesCleared++;
        }
    }

    private void ReconcileConstruct(in ObjectKey key, LocalBinding binding, in FactoryCombatState combat)
    {
        var entityId = key.NativeId;
        if (combat.HasConstructStat)
        {
            if (binding.ConstructStatId == 0)
            {
                binding.ConstructStatId = pools.AllocateConstructStat();
                statsAllocated++;
                pools.WriteConstructStat(binding.ConstructStatId, combat, entityId);
                pools.WriteEntityConstructStat(entityId, binding.ConstructStatId);
            }
            else
            {
                pools.WriteConstructStat(binding.ConstructStatId, combat, entityId);
            }
            return;
        }

        var current = pools.ReadEntityConstructStat(entityId);
        if (binding.ConstructStatId != 0)
        {
            if (current == binding.ConstructStatId)
            {
                pools.WriteEntityConstructStat(entityId, 0);
                referencesCleared++;
            }
            pools.ReleaseConstructStat(binding.ConstructStatId);
            statsReleased++;
            binding.ConstructStatId = 0;
        }
        else if (current != 0)
        {
            pools.WriteEntityConstructStat(entityId, 0);
            referencesCleared++;
        }
    }
}
