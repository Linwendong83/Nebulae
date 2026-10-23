using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Authority;

namespace NebulaeModel.Authority;

// Owns immutable canonical buffers and scalar identities only. Neither the game pools nor
// a mutable subscriber cursor can be read by the connection's encoding worker.
internal readonly struct DeferredAuthorityState
{
    public readonly ObjectKey Key;
    public readonly long Revision, BaseRevision;
    public readonly byte[] State, Previous;
    public DeferredAuthorityState(in ScopeEvent value, HostMemberRecord previous)
    {
        Key = value.Key; Revision = value.Revision; State = value.State;
        BaseRevision = previous?.Revision ?? 0; Previous = previous?.State;
    }
}

internal sealed class AuthorityDeltaCache
{
    private readonly ConcurrentDictionary<(PoolKind, byte[], byte[]), Lazy<byte[]>> patches = new();
    internal byte[] Get(in DeferredAuthorityState record)
    {
        var copy = record;
        return patches.GetOrAdd((record.Key.Kind, record.Previous, record.State),
            _ => new Lazy<byte[]>(() => DeferredAuthorityStates.Delta(copy), true)).Value;
    }
}

internal static class DeferredAuthorityStates
{
    internal static byte[] Delta(in DeferredAuthorityState record)
    {
        if (record.Previous == null) return null;
        FieldDeltaCodec.TryEncode(record.Previous, AuthorityStateFields.Describe(record.Key.Kind, record.Previous),
            record.State, AuthorityStateFields.Describe(record.Key.Kind, record.State), record.State.Length - 9, out var patch);
        return patch;
    }

    internal static AuthorityWorldStatePacket Build(DeferredAuthorityState[] inputs, AuthorityEnvelopeHeader header,
        ScopeKey scope, long baseline, long subscriptionEpoch, AuthorityDeltaCache patches)
    {
        var records = new AuthorityWorldStateRecord[inputs.Length];
        for (var i = 0; i < inputs.Length; i++)
        {
            var record = inputs[i];
            var patch = record.Previous == null ? null : patches == null ? Delta(record) : patches.Get(record);
            var delta = patch != null;
            records[i] = new AuthorityWorldStateRecord(record.Key, record.Revision, patch ?? record.State, delta ? record.BaseRevision : 0);
        }
        if (!AuthorityWorldStateCodec.TryEncode(records, out var bytes)) throw new InvalidOperationException("World state batch refused by its own codec.");
        header = new AuthorityEnvelopeHeader(header.Family, header.Epoch, header.Connection, header.Sequence,
            header.HostTick, header.ClaimedPlayerId, bytes.Length);
        var packet = AuthorityWorldStatePacket.Create(header, scope, baseline, records.Length, bytes);
        packet.SubscriptionEpoch = subscriptionEpoch;
        return packet;
    }
}
