using System;
using System.Collections.Generic;
using UnityEngine;

namespace NebulaeWorld.Player;

internal static class TerrainVegetationQuery
{
    // PlanetTimer factories have no populated collider chunks. Use the same native
    // collider geometry from vegetation data, independently of which peer renders it.
    internal static int Collect(VegeData[] pool, int cursor, Vector3 center, float radius, ref int[] ids,
        Func<short, ColliderData[]> resolve)
    {
        if (ids == null || ids.Length == 0) ids = new int[1024];
        else Array.Clear(ids, 0, ids.Length);
        if (pool == null) return 0;
        var shapes = new Dictionary<short, (ColliderData[] Data, float Reach)>();
        var count = 0;
        for (var id = 1; id < cursor && id < pool.Length; id++)
        {
            ref var vege = ref pool[id];
            if (vege.id != id) continue;
            if (!shapes.TryGetValue(vege.protoId, out var shape))
            {
                var data = resolve(vege.protoId); var reach = 0f;
                if (data != null)
                    foreach (var collider in data)
                        reach = Math.Max(reach, collider.pos.magnitude + collider.ext.magnitude);
                shapes[vege.protoId] = shape = (data, reach);
            }
            if (shape.Data == null) continue;
            var scale = Math.Max(Math.Abs(vege.scl.x), Math.Max(Math.Abs(vege.scl.y), Math.Abs(vege.scl.z)));
            var reachFromRoot = radius + shape.Reach * scale;
            if ((vege.pos - center).sqrMagnitude > reachFromRoot * reachFromRoot) continue;
            foreach (var source in shape.Data)
            {
                if (source.isForBuild) continue;
                var collider = source.BindToObject(id, 0, EObjectType.Vegetable, vege.pos, vege.rot, vege.scl);
                if ((collider.pos - center).sqrMagnitude > radius * radius + collider.ext.sqrMagnitude) continue;
                if (count == ids.Length) Array.Resize(ref ids, Math.Max(8, ids.Length * 2));
                ids[count++] = id;
                break;
            }
        }
        return count;
    }
}
