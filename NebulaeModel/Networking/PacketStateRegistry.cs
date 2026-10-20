using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking.Serialization;

namespace NebulaeModel.Networking;

internal enum PacketSyncKind { State, Operation, Snapshot, Control, Authority, Router }

internal sealed class SerializedState
{
    public byte[] Bytes { get; set; }
    public WireField[] Fields { get; set; }
    public byte[] Key { get; set; }
    public string CacheKey { get; set; }
}

/// <summary>Explicit policies use wire orders, not type or property names, after registration.</summary>
internal static partial class PacketStateRegistry
{
    private sealed class Policy
    {
        public PacketSyncKind Kind;
        public ConditionalWeakTable<byte[], SerializedState>.CreateValueCallback Describe;
    }
    private static readonly Dictionary<ulong, Policy> policies = new();
    private static readonly ConditionalWeakTable<byte[], SerializedState> descriptions = new();
    private static readonly NebulaeNetPacketProcessor decoder = new();
    private static readonly SerializedState ignored = new();

    static PacketStateRegistry()
    {
        PacketUtils.RegisterNestedTypes(decoder);
        RegisterPolicies();
    }

    public static bool Contains(Type type) => policies.ContainsKey(WireContract.PacketId(type));
    public static PacketSyncKind Kind(Type type) => policies[WireContract.PacketId(type)].Kind;
    public static bool IsState(ulong id) => policies.TryGetValue(id, out var p) && p.Describe != null;
    public static string CacheKey(ulong id, byte[] key) => id.ToString("X16") + ":" + Convert.ToBase64String(key);

    public static SerializedState Describe(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 8 || !policies.TryGetValue(BitConverter.ToUInt64(bytes, 0), out var policy) || policy.Describe == null) return null;
        if (descriptions.TryGetValue(bytes, out var cached)) return ReferenceEquals(cached, ignored) ? null : cached;
        var state = descriptions.GetValue(bytes, policy.Describe);
        return ReferenceEquals(state, ignored) ? null : state;
    }

    private static void Add<T>(PacketSyncKind kind) where T : class, new() =>
        policies.Add(WireContract.PacketId(typeof(T)), new Policy { Kind = kind });

    private static void State<T>(int[] keyOrders, Func<T, bool> eligible = null) where T : class, new()
    {
        var id = WireContract.PacketId(typeof(T));
        var properties = WireContract.Properties(typeof(T));
        var orders = properties.Select(p => p.GetCustomAttributes(typeof(WireOrderAttribute), true).Cast<WireOrderAttribute>().Single().Order).ToArray();
        var keys = keyOrders.Select(order => Array.IndexOf(orders, order)).ToArray();
        if (keys.Any(index => index < 0)) throw new InvalidTypeException("Invalid state identity for " + typeof(T));
        var types = properties.Select(p => p.PropertyType).ToArray();
        var widths = types.Select(FixedWidth).ToArray();
        // These packets can never amortize a field envelope. Do not deserialize or cache them
        // just to rediscover that on every clock update, fluid change or pause notification.
        if (widths.All(width => width > 0) && widths.Sum() + 8 <=
            48 + keys.Sum(index => widths[index]) + 1 + (properties.Length + 8) / 8)
        {
            policies[id] = new Policy { Kind = PacketSyncKind.State };
            return;
        }
        if (eligible == null && widths.All(width => width > 0))
        {
            var fields = new List<WireField> { new(0, 8) };
            var keyFields = new List<WireField>();
            var offset = 8;
            for (var i = 0; i < types.Length; i++)
            {
                if (keys.Contains(i)) keyFields.Add(new WireField(offset, widths[i]));
                var unit = types[i] == typeof(Float3) || types[i] == typeof(Float4) ? 4 : types[i] == typeof(Double3) ? 8 : widths[i];
                for (var part = 0; part < widths[i]; part += unit) fields.Add(new WireField(offset + part, unit));
                offset += widths[i];
            }
            var layout = fields.ToArray(); var identityFields = keyFields.ToArray();
            var size = offset; var keySize = identityFields.Sum(field => field.Length);
            policies[id] = new Policy { Kind = PacketSyncKind.State, Describe = bytes =>
            {
                if (bytes.Length != size) throw new InvalidDataException("Invalid fixed state size.");
                var identity = new byte[keySize]; var position = 0;
                foreach (var field in identityFields)
                { Buffer.BlockCopy(bytes, field.Offset, identity, position, field.Length); position += field.Length; }
                return new SerializedState { Bytes = bytes, Fields = layout, Key = identity, CacheKey = CacheKey(id, identity) };
            } };
            return;
        }
        policies[id] = new Policy { Kind = PacketSyncKind.State, Describe = bytes =>
        {
            T packet; int[] ends;
            lock (decoder) packet = decoder.ReadFieldLayout<T>(bytes, out ends);
            if (eligible != null && !eligible(packet)) return ignored;
            if (ends.Length != types.Length) throw new InvalidDataException("Invalid state fields.");
            var fields = new List<WireField> { new(0, 8) };
            using var key = new MemoryStream();
            var offset = 8;
            for (var i = 0; i < ends.Length; i++)
            {
                var length = ends[i] - offset;
                if (keys.Contains(i)) key.Write(bytes, offset, length);
                var type = types[i];
                var unit = type == typeof(Float3) || type == typeof(Float4) ? 4 : type == typeof(Double3) ? 8 : 0;
                if (unit > 0)
                {
                    for (var part = 0; part < length; part += unit) fields.Add(new WireField(offset + part, unit));
                }
                else
                {
                    var element = type.IsArray ? PrimitiveSize(type.GetElementType()) : 0;
                    fields.Add(new WireField(offset, length, element, element == 0 ? 0 : 4));
                }
                offset = ends[i];
            }
            var identity = key.ToArray();
            if (identity.Length > 4096 || !FieldDeltaCodec.ValidLayout(bytes, fields)) throw new InvalidDataException("Invalid state layout.");
            return new SerializedState { Bytes = bytes, Fields = fields.ToArray(), Key = identity, CacheKey = CacheKey(id, identity) };
        } };
    }

    private static int PrimitiveSize(Type type) => type == typeof(byte) || type == typeof(sbyte) || type == typeof(bool) ? 1 :
        type == typeof(short) || type == typeof(ushort) || type == typeof(char) ? 2 :
        type == typeof(int) || type == typeof(uint) || type == typeof(float) ? 4 :
        type == typeof(long) || type == typeof(ulong) || type == typeof(double) ? 8 : 0;

    private static int FixedWidth(Type type) => type.IsEnum ? PrimitiveSize(Enum.GetUnderlyingType(type)) :
        type == typeof(Float3) ? 12 : type == typeof(Float4) ? 16 : type == typeof(Double3) ? 24 : PrimitiveSize(type);

    static partial void RegisterPolicies();
}
