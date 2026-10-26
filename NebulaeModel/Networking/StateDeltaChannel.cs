using System;
using System.Collections.Generic;
using System.IO;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Routers;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Session;
using NebulaeModel.Logger;

namespace NebulaeModel.Networking;

/// <summary>Field baselines belong to the final recipient, after routing and join capture.</summary>
internal sealed class StateDeltaChannel
{
    private static readonly ulong deltaId = WireContract.PacketId(typeof(StateDeltaPacket));
    private static readonly ulong requestId = WireContract.PacketId(typeof(StateResyncPacket));
    private static readonly ulong planetId = WireContract.PacketId(typeof(PlanetBroadcastPacket));
    private static readonly ulong starId = WireContract.PacketId(typeof(StarBroadcastPacket));
    private readonly object sendGate = new(), receiveGate = new();
    private readonly Cache outgoing, incoming;
    private readonly HashSet<string> pending = new();
    private readonly Action<byte[]> sendControl;
    private readonly Action<Func<byte[]>> sendLatest;
    private readonly NebulaeNetPacketProcessor routerWriter = new(), routerReader = new();
    private bool stopped;
    private readonly DiagnosticWatchdog diagnosticWaits = new();
    private long repairsCompleted;
    public void CheckDiagnostics(int connectionId)
    {
        lock (receiveGate)
            diagnosticWaits.Observe("delta-repair:" + connectionId, pending.Count > 0,
                () => $"[network] field state repair pending connection={connectionId} pending={pending.Count} incomingCacheBytes={incoming.Bytes}", progress: repairsCompleted);
    }

    public StateDeltaChannel(Action<byte[]> sendControl = null, Action<Func<byte[]>> sendLatest = null,
        int maxCacheBytes = 16 * 1024 * 1024)
    {
        this.sendControl = sendControl; this.sendLatest = sendLatest;
        outgoing = new Cache(maxCacheBytes); incoming = new Cache(maxCacheBytes);
        foreach (var codec in new[] { routerWriter, routerReader })
        {
            PacketUtils.RegisterNestedTypes(codec);
            codec.SubscribeReusable<PlanetBroadcastPacket>(_ => { });
            codec.SubscribeReusable<StarBroadcastPacket>(_ => { });
        }
    }

    public long OutgoingCacheBytes { get { lock (sendGate) return outgoing.Bytes; } }
    public long IncomingCacheBytes { get { lock (receiveGate) return incoming.Bytes; } }
    public int PendingRepairs { get { lock (receiveGate) return pending.Count; } }

    public byte[] Prepare(byte[] bytes)
    {
        lock (sendGate) return stopped ? null : Encode(bytes, 0);
    }

    public byte[] Receive(byte[] bytes)
    {
        try { lock (receiveGate) return stopped ? null : Decode(bytes, 0); }
        catch (Exception error) when (error is IOException or ParseException or ArgumentException or OverflowException or IndexOutOfRangeException)
        { throw new InvalidDataException("Malformed state encoding.", error); }
    }

    public void Clear()
    {
        lock (sendGate) { stopped = true; outgoing.Clear(); }
        lock (receiveGate) { incoming.Clear(); pending.Clear(); diagnosticWaits.Clear(); }
    }

    private byte[] Encode(byte[] bytes, int depth, RouteScope[] routes = null)
    {
        if (bytes == null) return null;
        if (bytes.Length < 8) throw new InvalidDataException("Missing packet identity.");
        var id = BitConverter.ToUInt64(bytes, 0);
        if (id == planetId || id == starId) return Route(bytes, true, depth, routes);
        var state = PacketStateRegistry.Describe(bytes);
        if (state == null) return bytes;
        var previous = outgoing.Get(state.CacheKey);
        if (previous != null && previous.Revision < long.MaxValue &&
            FieldDeltaCodec.TryEncode(previous.State.Bytes, previous.State.Fields, bytes, state.Fields,
                bytes.Length - 49 - state.Key.Length, out var patch))
        {
            var encoded = WriteDelta(id, state.Key, previous.Baseline, previous.Revision, patch);
            if (encoded.Length < bytes.Length)
            {
                outgoing.Put(state, previous.Baseline, previous.Revision + 1, routes);
                return encoded;
            }
        }
        outgoing.Put(state, AuthoritySnapshotCodec.Hash(bytes), 1, routes);
        return bytes;
    }

    private byte[] Decode(byte[] bytes, int depth)
    {
        if (bytes == null || bytes.Length < 8) throw new InvalidDataException("Missing packet identity.");
        var id = BitConverter.ToUInt64(bytes, 0);
        if (id == planetId || id == starId) return Route(bytes, false, depth);
        if (id != deltaId && id != requestId)
        {
            var full = PacketStateRegistry.Describe(bytes);
            if (full != null)
            {
                incoming.Put(full, AuthoritySnapshotCodec.Hash(bytes), 1);
                if (pending.Remove(full.CacheKey)) { repairsCompleted++; Log.Debug($"[network] field state repaired packet={WireContract.PacketName(id)} id={id} bytes={bytes.Length} pending={pending.Count}"); }
            }
            return bytes;
        }
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        reader.ReadUInt64();
        var original = reader.ReadUInt64();
        if (!PacketStateRegistry.IsState(original)) throw new InvalidDataException("Field encoding targets an operation or unknown packet.");
        var keyLength = reader.ReadInt32();
        if (keyLength < 0 || keyLength > 4096) throw new InvalidDataException("Invalid state identity size.");
        var key = FieldDeltaCodec.ReadExact(reader, keyLength);
        var cacheKey = PacketStateRegistry.CacheKey(original, key);
        if (id == requestId)
        {
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing state repair bytes.");
            // Resolve at the queue position, not when the request arrived: newer queued states
            // must never be followed by a repair containing an older absolute value.
            sendLatest?.Invoke(() =>
            {
                lock (sendGate)
                {
                    var latest = outgoing.Get(cacheKey);
                    if (latest == null)
                    {
                        Diagnostics.Warn("delta:repair-unavailable:" + original, () => $"[network] field repair unavailable packet={WireContract.PacketName(original)} id={original} keyBytes={keyLength} outgoingCacheBytes={outgoing.Bytes}");
                        return null;
                    }
                    outgoing.Remove(cacheKey);
                    var full = latest.State.Bytes;
                    if (latest.Routes != null)
                        for (var i = latest.Routes.Length - 1; i >= 0; i--)
                        {
                            var route = latest.Routes[i];
                            full = route.Planet ? routerWriter.Write(new PlanetBroadcastPacket(new[] { full }, route.Scope)) :
                                routerWriter.Write(new StarBroadcastPacket(new[] { full }, route.Scope));
                        }
                    return full;
                }
            });
            return null;
        }
        var baseline = reader.ReadUInt64(); var from = reader.ReadInt64(); var to = reader.ReadInt64();
        if (baseline == 0 || from <= 0 || from == long.MaxValue || to != from + 1) throw new InvalidDataException("Invalid field revision.");
        var length = reader.ReadInt32();
        if (length < 0 || length > FieldDeltaCodec.MaxBytes) throw new InvalidDataException("Invalid field payload size.");
        var data = FieldDeltaCodec.ReadExact(reader, length);
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing state bytes.");
        var previous = incoming.Get(cacheKey);
        if (previous == null || previous.Baseline != baseline || previous.Revision != from || pending.Contains(cacheKey))
        {
            if (pending.Count >= 4096 && !pending.Contains(cacheKey)) throw new InvalidDataException("Too many pending state repairs.");
            if (pending.Add(cacheKey))
            {
                Log.Debug($"[network] field repair requested packet={WireContract.PacketName(original)} id={original} keyBytes={keyLength} baseline={baseline} localBaseline={previous?.Baseline} expectedRevision={from} localRevision={previous?.Revision} pending={pending.Count}");
                sendControl?.Invoke(WriteRequest(original, key));
            }
            return null;
        }
        var restored = FieldDeltaCodec.Apply(previous.State.Bytes, previous.State.Fields, data);
        var current = PacketStateRegistry.Describe(restored);
        if (current == null || current.CacheKey != cacheKey || BitConverter.ToUInt64(restored, 0) != original)
            throw new InvalidDataException("Field patch changed its identity.");
        incoming.Put(current, baseline, to);
        return restored;
    }

    private byte[] Route(byte[] bytes, bool sending, int depth, RouteScope[] routes = null)
    {
        if (depth >= 8) throw new InvalidDataException("State envelope nesting limit exceeded.");
        var codec = sending ? routerWriter : routerReader;
        var planet = BitConverter.ToUInt64(bytes, 0) == planetId;
        var scope = 0;
        byte[][] children;
        if (planet)
        { var packet = codec.ReadFieldLayout<PlanetBroadcastPacket>(bytes, out _); scope = packet.PlanetId; children = packet.PacketObjects; }
        else
        { var packet = codec.ReadFieldLayout<StarBroadcastPacket>(bytes, out _); scope = packet.StarId; children = packet.PacketObjects; }
        if (children == null || children.Length > PlanetBroadcastPacket.MaxPacketObjects) throw new InvalidDataException("Invalid state envelope.");
        RouteScope[] path = null;
        if (sending)
        {
            path = new RouteScope[(routes?.Length ?? 0) + 1];
            if (routes != null) Array.Copy(routes, path, routes.Length);
            path[path.Length - 1] = new RouteScope(planet, scope);
        }
        List<byte[]> result = null;
        for (var i = 0; i < children.Length; i++)
        {
            var child = children[i];
            var transformed = child == null || child.Length == 0 ? child :
                sending ? Encode(child, depth + 1, path) : Decode(child, depth + 1);
            if (result == null && !ReferenceEquals(child, transformed))
            {
                result = new List<byte[]>(children.Length);
                for (var j = 0; j < i; j++) result.Add(children[j]);
            }
            if (result != null && (transformed != null || child == null)) result.Add(transformed);
        }
        if (result == null) return bytes;
        return planet ? codec.Write(new PlanetBroadcastPacket(result.ToArray(), scope)) : codec.Write(new StarBroadcastPacket(result.ToArray(), scope));
    }

    private static byte[] WriteDelta(ulong id, byte[] key, ulong baseline, long from, byte[] patch)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(deltaId); writer.Write(id); writer.Write(key.Length); writer.Write(key);
        writer.Write(baseline); writer.Write(from); writer.Write(from + 1); writer.Write(patch.Length); writer.Write(patch);
        return stream.ToArray();
    }
    private static byte[] WriteRequest(ulong id, byte[] key)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(requestId); writer.Write(id); writer.Write(key.Length); writer.Write(key);
        return stream.ToArray();
    }

    private readonly struct RouteScope(bool planet, int scope)
    {
        public bool Planet { get; } = planet;
        public int Scope { get; } = scope;
    }

    private sealed class Entry
    {
        public SerializedState State;
        public ulong Baseline;
        public long Revision;
        public LinkedListNode<string> Node;
        public RouteScope[] Routes;
        public int Cost => State.Bytes.Length + State.Key.Length + State.Fields.Length * 16 + (Routes?.Length ?? 0) * 8 + 128;
    }
    private sealed class Cache(int maxBytes)
    {
        private readonly Dictionary<string, Entry> entries = new();
        private readonly LinkedList<string> lru = new();
        public long Bytes { get; private set; }
        public Entry Get(string key)
        {
            if (!entries.TryGetValue(key, out var entry)) return null;
            lru.Remove(entry.Node); lru.AddLast(entry.Node); return entry;
        }
        public void Remove(string key)
        {
            if (!entries.TryGetValue(key, out var entry)) return;
            entries.Remove(key); lru.Remove(entry.Node); Bytes -= entry.Cost;
        }
        public void Put(SerializedState state, ulong baseline, long revision, RouteScope[] routes = null)
        {
            if (entries.TryGetValue(state.CacheKey, out var entry))
            { lru.Remove(entry.Node); entries.Remove(state.CacheKey); Bytes -= entry.Cost; }
            else entry = new Entry();
            entry.State = state; entry.Baseline = baseline; entry.Revision = revision; entry.Routes = routes;
            if (entry.Cost > maxBytes) return;
            while (lru.Count >= 65536 || Bytes + entry.Cost > maxBytes) Remove(lru.First.Value);
            if (entry.Node == null) entry.Node = new LinkedListNode<string>(state.CacheKey);
            lru.AddLast(entry.Node); entries.Add(state.CacheKey, entry); Bytes += entry.Cost;
        }
        public void Clear() { entries.Clear(); lru.Clear(); Bytes = 0; }
    }
}
