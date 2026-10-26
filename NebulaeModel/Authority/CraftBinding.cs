#region

using System;
using System.Collections.Generic;
using NebulaeModel.Packets.Authority;

#endregion

namespace NebulaeModel.Authority;

/// <summary>
/// The narrow view of a client's local craft pool that the craft binding writes through.
/// </summary>
/// <remarks>
/// The binding's decisions (create, update, release) are model logic and run in plain tests;
/// touching vanilla pools is the game adapter's job. Host pool indexes never cross this seam as raw
/// integers to be trusted — the <see cref="ObjectKey"/> carries identity and generation, and the
/// local slot is the host's <c>NativeId</c> installed at the same position, with
/// cursor/recycle maintained by the game adapter. The client never allocates an authoritative slot
/// on its own. Logic component ids (fleet/unit/drone/vehicle) stay zero on the shell, so the
/// vanilla fleet, targeting, ammo and destruction ticks skip it by construction.
/// </remarks>
internal interface ICraftPools
{
    /// <summary>True when the local craft slot exists and is live.</summary>
    bool CraftExists(int craftId);

    /// <summary>Creates a host-identified craft; native local motion is separate from shared combat rules.</summary>
    void CreateCraftShell(int craftId, in CraftState state);

    /// <summary>Overwrites the shell's absolute pose/HP/owner from the host record.</summary>
    void WriteCraftState(int craftId, in CraftState state);

    /// <summary>Pure removal (no drops, statistics or ammo refunds). Never a destruction rule.</summary>
    void RemoveCraftShell(int craftId);
}

internal interface ILocalCraftPools : ICraftPools
{
    bool TryGetLocalId(int hostId, out int localId);
}

/// <summary>
/// Rebuilds client-local craft from host facts.
/// </summary>
/// <remarks>
/// <para>
/// One instance serves one (kind, scope) pair: ground craft on one planet, or space craft in sector
/// scope 0. Keyed by <see cref="ObjectKey"/> — identity, not slot number. A recycled host slot
/// arrives under a new generation and gets a fresh shell; the old key's shell is released with its
/// key. Two players' craft never share a slot and never overwrite each other because every record
/// is keyed by object, not by owner: the owner is an attribute the binding stores, not a second
/// index.
/// </para>
/// <para>
/// The <c>isSpace</c> flag must agree with the binding's kind: a ground binding refuses a space
/// record and vice versa. That is how a pool mix-up (ground slot installed as space craft) fails
/// loudly instead of building a shell in the wrong pool.
/// </para>
/// </remarks>
internal sealed class CraftBinding
{
    private readonly ICraftPools pools;
    private readonly PoolKind expectedKind;
    private readonly int expectedScope;
    private readonly Dictionary<ObjectKey, CraftState> bindings = [];
    private readonly Dictionary<int, ObjectKey> slotOwner = [];
    private long statesRefused;
    private long membersRemoved;

    public CraftBinding(PoolKind kind, int scope, ICraftPools pools)
    {
        if (kind != PoolKind.GroundCraft && kind != PoolKind.SpaceCraft)
        {
            throw new ArgumentException("A craft binding serves GroundCraft or SpaceCraft.", nameof(kind));
        }
        if (!AuthorityScope.IsValidScope(kind, scope))
        {
            throw new ArgumentException("Scope does not match the craft pool kind.", nameof(scope));
        }
        expectedKind = kind;
        expectedScope = scope;
        this.pools = pools ?? throw new ArgumentNullException(nameof(pools));
    }

    public int BoundCount => bindings.Count;
    public long StatesRefused => statesRefused;
    public long MembersRemoved => membersRemoved;

    public bool ApplyState(in ObjectKey key, byte[] state)
    {
        if (key.Kind != expectedKind || key.Scope != expectedScope)
        {
            statesRefused++;
            BindingDiagnostics.Refused("invalid-state-or-identity", key, state?.Length ?? 0);
            return false;
        }
        if (state == null || !CraftStateCodec.TryDecode(state, 0, state.Length, out var decoded, out _))
        {
            statesRefused++;
            BindingDiagnostics.Refused("invalid-state-or-identity", key, state?.Length ?? 0);
            return false;
        }
        var wantSpace = expectedKind == PoolKind.SpaceCraft;
        if (decoded.IsSpace != wantSpace)
        {
            statesRefused++;
            BindingDiagnostics.Refused("invalid-state-or-identity", key, state?.Length ?? 0);
            return false;
        }
        return ApplyDecoded(key, in decoded);
    }

    private bool ApplyDecoded(in ObjectKey key, in CraftState decoded)
    {
        if (!bindings.ContainsKey(key))
        {
            bindings[key] = decoded;
            // Same slot-ownership rule as the enemy bindings: a recycled slot's trailing despawn
            // must not delete the new generation's shell.
            slotOwner[key.NativeId] = key;
            pools.CreateCraftShell(key.NativeId, in decoded);
        }
        else
        {
            bindings[key] = decoded;
            slotOwner[key.NativeId] = key;
            pools.WriteCraftState(key.NativeId, in decoded);
        }
        return true;
    }

    public void RemoveMember(in ObjectKey key)
    {
        if (!bindings.Remove(key))
        {
            return;
        }
        if (slotOwner.TryGetValue(key.NativeId, out var owner) && owner.Equals(key))
        {
            slotOwner.Remove(key.NativeId);
            if (pools.CraftExists(key.NativeId))
            {
                pools.RemoveCraftShell(key.NativeId);
            }
        }
        membersRemoved++;
    }

    public void ReconcileBaseline(IReadOnlyList<SnapshotMemberRecord> members)
    {
        var keep = new HashSet<ObjectKey>();
        foreach (var member in members)
        {
            keep.Add(member.Key);
        }
        List<ObjectKey> doomed = null;
        foreach (var key in bindings.Keys)
        {
            if (!keep.Contains(key))
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
}
