#region

using System;

#endregion

namespace NebulaeModel.Attributes;

/// <summary>
/// BepInEx configuration key of an option property. The key is what users' existing
/// nebulae.cfg stores, so it is spelled out here instead of being derived from the
/// property name, which the release build is free to rename.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal class ConfigKeyAttribute : Attribute
{
    public readonly string Key;

    public ConfigKeyAttribute(string key)
    {
        Key = key;
    }
}
