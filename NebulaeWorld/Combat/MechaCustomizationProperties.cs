using System;

namespace NebulaeWorld.Combat;

/// <summary>Derives combat properties from the applied armor using the native material rules.</summary>
internal static class MechaCustomizationProperties
{
    public static void Refresh(global::Player player, bool preserveRatio)
    {
        var mecha = player?.mecha;
        var bones = player?.mechaArmorModel?.boneModels;
        if (mecha?.appearance == null || bones == null) return;
        var properties = new MechaArmorPropertyDesc();
        mecha.appearance.CalculateProperty(bones, properties);
        Apply(mecha, properties, preserveRatio);
    }

    internal static void Apply(Mecha mecha, MechaArmorPropertyDesc properties, bool preserveRatio)
    {
        if (double.IsNaN(properties.durability) || double.IsInfinity(properties.durability) ||
            properties.durability < 1 || properties.durability > int.MaxValue - 1 ||
            double.IsNaN(properties.consumption) || double.IsInfinity(properties.consumption) || properties.consumption <= 0) return;
        var hp = mecha.hp;
        var oldMax = mecha.hpMaxApplied;
        var ratio = oldMax > 0 ? Math.Max(0, Math.Min(1, (double)hp / oldMax)) : hp > 0 ? 1 : 0;
        mecha.hpMax = (int)(properties.durability + 0.5);
        mecha.hp = preserveRatio ? (int)(mecha.hpMaxApplied * ratio + 0.5) : Math.Min(hp, mecha.hpMaxApplied);
        mecha.energyConsumptionCoef = properties.consumption;
    }
}
