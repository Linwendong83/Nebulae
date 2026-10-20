#region

using System;
using System.Collections.Generic;

#endregion

namespace NebulaeModel.Authority;

/// <summary>One member's contribution to a canonical scope digest.</summary>
/// <remarks>
/// The digest covers exactly what the host's member table and the client's mirrors both hold:
/// identity (the full <see cref="ObjectKey"/> with its generation), the revision the state was
/// published at, and the canonical state bytes. Positions, GPU handles and anything display-only
/// are deliberately absent — "不能用双方当前不同 tick 的位置或 GPU 字段直接比较".
/// </remarks>
internal readonly struct ScopeDigestMember
{
    public ScopeDigestMember(in ObjectKey key, long revision, byte[] state)
    {
        Key = key;
        Revision = revision;
        State = state;
    }

    public ObjectKey Key { get; }

    public long Revision { get; }

    /// <summary>Canonical state bytes, or null while no state has been published.</summary>
    public byte[] State { get; }
}

/// <summary>The digest statement one side holds for a scope, bound to the stream position it
/// describes.</summary>
/// <remarks>
/// A digest is only comparable at the stream sequence it declares: the host composes it
/// at one capture tick and one log position, and the replica may only verify it once it has applied
/// exactly that stream position — an earlier one is a future fact, a later one is already superseded.
/// </remarks>
internal readonly struct ScopeDigestState
{
    public ScopeDigestState(ScopeKey scope, long subscriptionEpoch, long baselineId, long hostTick,
        long declaredStreamSequence, int memberCount, ulong digest)
    {
        Scope = scope;
        SubscriptionEpoch = subscriptionEpoch;
        BaselineId = baselineId;
        HostTick = hostTick;
        DeclaredStreamSequence = declaredStreamSequence;
        MemberCount = memberCount;
        Digest = digest;
    }

    public ScopeKey Scope { get; }

    public long SubscriptionEpoch { get; }

    /// <summary>Baseline the digested membership belongs to; 0 for a digest-only observation.</summary>
    public long BaselineId { get; }

    public long HostTick { get; }

    /// <summary>Stream sequence the digest describes. The replica must have applied exactly it.</summary>
    public long DeclaredStreamSequence { get; }

    public int MemberCount { get; }

    public ulong Digest { get; }
}

/// <summary>
/// Computes the canonical digest of one scope's membership.
/// </summary>
/// <remarks>
/// <para>
/// The computation is deterministic and order-independent: both sides enumerate a dictionary whose
/// order they do not control, so the members are sorted by identity before folding. The fold is
/// FNV-1a 64 — the same non-cryptographic "sender and receiver hold the same bytes" check the
/// snapshot image uses. It is a divergence detector, not a security boundary.
/// </para>
/// <para>
/// A member with no published state and a member with empty state fold differently (a marker byte
/// separates them), because "identity without state" and "identity with empty state" are different
/// facts the replica distinguishes elsewhere too.
/// </para>
/// </remarks>
internal static class ScopeDigestComputer
{
    /// <summary>Computes the digest over one scope's members. The list is not modified.</summary>
    public static ulong Compute(List<ScopeDigestMember> members)
    {
        if (members == null) throw new ArgumentNullException(nameof(members));

        // Sort a shallow copy: dictionary enumeration order differs between processes, so the
        // digest may only depend on the member contents.
        var ordered = new ScopeDigestMember[members.Count];
        for (var i = 0; i < members.Count; i++) ordered[i] = members[i];
        Array.Sort(ordered, (a, b) =>
        {
            var ka = a.Key;
            var kb = b.Key;
            var byKind = ka.Kind.CompareTo(kb.Kind);
            if (byKind != 0) return byKind;
            var byScope = ka.Scope.CompareTo(kb.Scope);
            if (byScope != 0) return byScope;
            var byNative = ka.NativeId.CompareTo(kb.NativeId);
            if (byNative != 0) return byNative;
            return ka.Generation.CompareTo(kb.Generation);
        });

        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var member in ordered)
        {
            var key = member.Key;
            hash = FoldByte(hash, prime, (byte)key.Kind);
            hash = FoldInt(hash, prime, key.Scope);
            hash = FoldInt(hash, prime, key.NativeId);
            hash = FoldLong(hash, prime, key.Generation);
            hash = FoldLong(hash, prime, (long)key.Epoch.High);
            hash = FoldLong(hash, prime, (long)key.Epoch.Low);
            hash = FoldLong(hash, prime, member.Revision);
            if (member.State == null)
            {
                hash = FoldByte(hash, prime, 0x00);
            }
            else
            {
                hash = FoldByte(hash, prime, 0x01);
                hash = FoldInt(hash, prime, member.State.Length);
                foreach (var b in member.State)
                {
                    hash = (hash ^ b) * prime;
                }
            }
        }
        return hash;
    }

    private static ulong FoldByte(ulong hash, ulong prime, byte value) => (hash ^ value) * prime;

    private static ulong FoldInt(ulong hash, ulong prime, int value)
    {
        hash = (hash ^ (byte)value) * prime;
        hash = (hash ^ (byte)(value >> 8)) * prime;
        hash = (hash ^ (byte)(value >> 16)) * prime;
        hash = (hash ^ (byte)(value >> 24)) * prime;
        return hash;
    }

    private static ulong FoldLong(ulong hash, ulong prime, long value)
    {
        var bits = (ulong)value;
        for (var shift = 0; shift < 64; shift += 8)
        {
            hash = (hash ^ (byte)(bits >> shift)) * prime;
        }
        return hash;
    }
}
