using System;
using System.Reflection;

namespace NebulaeModel.Utils;

internal static class NativeGameAccess
{
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
