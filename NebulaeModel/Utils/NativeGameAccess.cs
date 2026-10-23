using System;
using System.Reflection;

namespace NebulaeModel.Utils;

internal static class NativeGameAccess
{
    private static readonly FieldInfo GalaxyDescription = typeof(UIGalaxySelect).GetField("gameDesc", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static GameDesc GetGalaxyDescription(UIGalaxySelect select) => (GameDesc)GalaxyDescription.GetValue(select);
    internal static void SetGalaxyDescription(UIGalaxySelect select, GameDesc desc) => GalaxyDescription.SetValue(select, desc);

    public static void SetHiddenProperty(this object instance, string propertyName, object value)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property?.GetSetMethod(true) == null) continue;
            property.SetValue(instance, value);
            return;
        }
        throw new MissingMemberException(instance.GetType().FullName, propertyName);
    }
}
