using System;
using System.Collections.Generic;

namespace NebulaeModel.Authority;

internal interface IReplicaFrameObserver
{
    void BeginReplicaFrame();
    void EndReplicaFrame(Action<ScopeKey, Action> apply);
}

// Logical/native states are installed immediately. Display writes can be merged until
// the frame boundary completes, when no simulation worker has observed an intermediate pose.
internal sealed class ReplicaPresentationWrites
{
    private readonly Dictionary<int, byte> pending = new();
    internal int Count => pending.Count;
    internal void Clear() => pending.Clear();
    internal void Add(int id, bool moved, bool rotated)
    {
        pending.TryGetValue(id, out var flags);
        pending[id] = (byte)(flags | (moved ? 1 : 0) | (rotated ? 2 : 0));
    }
    internal void FlushOne(int id, Action<int, bool, bool> write)
    {
        if (!pending.TryGetValue(id, out var flags)) return;
        pending.Remove(id); write(id, (flags & 1) != 0, (flags & 2) != 0);
    }
    internal void Flush(Action<int, bool, bool> write)
    {
        try { foreach (var pair in pending) write(pair.Key, (pair.Value & 1) != 0, (pair.Value & 2) != 0); }
        finally { pending.Clear(); }
    }
}
