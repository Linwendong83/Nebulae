using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NebulaeModel.Networking.Serialization;

namespace NebulaeModel.Networking;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class PacketIdAttribute(ulong id) : Attribute
{
    internal ulong Id { get; } = id;
}

[AttributeUsage(AttributeTargets.Property, Inherited = true)]
internal sealed class WireOrderAttribute(int order) : Attribute
{
    internal int Order { get; } = order;
}

internal static class WireContract
{
    private static readonly Dictionary<ulong, Type> ids = new();
    private static readonly object gate = new();

    internal static ulong PacketId(Type type)
    {
        var attribute = type.GetCustomAttribute<PacketIdAttribute>(false);
        if (attribute == null || attribute.Id == 0)
            throw new InvalidTypeException("A packet must declare a nonzero fixed identifier: " + type);
        lock (gate)
        {
            if (ids.TryGetValue(attribute.Id, out var previous) && previous != type)
                throw new InvalidTypeException("Duplicate packet identifier: " + attribute.Id);
            ids[attribute.Id] = type;
        }
        return attribute.Id;
    }

    internal static PropertyInfo[] Properties(Type type)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetGetMethod() != null && p.GetSetMethod() != null &&
                        !Attribute.IsDefined(p, typeof(IgnoreDataMemberAttribute))).ToArray();
        var orders = new HashSet<int>();
        foreach (var property in props)
        {
            var order = property.GetCustomAttribute<WireOrderAttribute>(true);
            if (order == null || order.Order < 0 || property.GetIndexParameters().Length != 0)
                throw new InvalidTypeException("A serialized property must declare a fixed order: " + type + "." + property.Name);
            if (!orders.Add(order.Order))
                throw new InvalidTypeException("Duplicate serialized property order: " + type + "." + property.Name);
        }
        return props.OrderBy(p => p.GetCustomAttribute<WireOrderAttribute>(true).Order).ToArray();
    }
}

internal static class SessionSentinels
{
    internal const int PLANET_NONE = -2;
    internal const int AUTHOR_NONE = -1;
    internal const int STAR_NONE = -1;
}
