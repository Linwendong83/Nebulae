using System.Collections.Generic;
using UnityEngine;

namespace NebulaeWorld.Factory;

internal static class GeothermalPlacement
{
    public static bool Validate(PlanetFactory factory, IEnumerable<BuildPreview> previews)
    {
        HashSet<int> available = null;
        foreach (var preview in previews)
        {
            if (preview.desc?.geothermal != true || preview.paramCount == 0) continue;
            if (preview.parameters == null || preview.parameters.Length == 0) return false;
            var id = preview.parameters[0];
            if (id == 0) continue; // Native lava-planet geothermal placement.
            if (id < 0 || id >= factory.ruinCursor || factory.ruinPool[id].id != id || factory.ruinPool[id].modelIndex != 406) return false;
            available ??= new HashSet<int>(factory.GetRemovableDFGBaseRuinIds());
            if (!available.Remove(id)) return false;
            var expected = factory.planet.aux.Snap(factory.ruinPool[id].pos, true);
            if (!((expected - preview.lpos).sqrMagnitude <= .01f)) return false;
        }
        return true;
    }
}
