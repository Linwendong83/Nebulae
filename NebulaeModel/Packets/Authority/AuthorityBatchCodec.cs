using NebulaeModel.Authority;

namespace NebulaeModel.Packets.Authority;

/// <summary>Shared identity prefix for a batch whose records all belong to one world and scope.</summary>
internal static class AuthorityBatchCodec
{
    public const int PrefixBytes = 4; // count
    public const int ScopeBytes = AuthorityEpoch.SizeBytes + 1 + 4;

    public static void WriteScope(AuthorityPayloadWriter writer, in ObjectKey key)
    {
        writer.WriteLong(unchecked((long)key.Epoch.High));
        writer.WriteLong(unchecked((long)key.Epoch.Low));
        writer.WriteByte((byte)key.Kind);
        writer.WriteInt(key.Scope);
    }

    public static bool TryReadScope(ref AuthorityPayloadReader reader, out ObjectKey shared)
    {
        shared = default;
        if (!reader.TryReadLong(out var high) || !reader.TryReadLong(out var low) ||
            !reader.TryReadByte(out var kind) || !reader.TryReadInt(out var scope)) return false;
        shared = new ObjectKey(new AuthorityEpoch(unchecked((ulong)high), unchecked((ulong)low)),
            (PoolKind)kind, scope, 0, 0);
        return true;
    }

    public static bool SameScope(in ObjectKey shared, in ObjectKey key) =>
        shared.Epoch.Equals(key.Epoch) && shared.Kind == key.Kind && shared.Scope == key.Scope;

    public static void WriteKey(AuthorityPayloadWriter writer, in ObjectKey key)
    {
        writer.WriteInt(key.NativeId);
        writer.WriteLong(key.Generation);
    }

    public static bool TryReadKey(ref AuthorityPayloadReader reader, in ObjectKey shared, out ObjectKey key)
    {
        key = default;
        if (!reader.TryReadInt(out var nativeId) || !reader.TryReadLong(out var generation)) return false;
        key = new ObjectKey(shared.Epoch, shared.Kind, shared.Scope, nativeId, generation);
        return true;
    }
}
