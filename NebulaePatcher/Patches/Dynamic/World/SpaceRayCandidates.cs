using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic.World;

// The native narrow phase and ordering are retained, including its fixed 3.6M star sphere.
// This immutable grid indexes canonical, stationary star centers, never GPU alias IDs.
internal sealed class GalaxySpatialIndex
{
    private const double Cell = 8000000.0;
    private readonly Dictionary<(long, long, long), List<int>> cells = new();
    internal GalaxySpatialIndex(GalaxyData galaxy)
    {
        foreach (var star in galaxy.stars)
        {
            var p = star.uPosition;
            var key = Key(p);
            if (!cells.TryGetValue(key, out var ids)) cells.Add(key, ids = new List<int>());
            ids.Add(star.id);
        }
    }
    private static (long, long, long) Key(VectorLF3 p) =>
        ((long)Math.Floor(p.x / Cell), (long)Math.Floor(p.y / Cell), (long)Math.Floor(p.z / Cell));

    internal bool Query(VectorLF3 origin, VectorLF3 direction, double distance, List<int> result)
    {
        result.Clear();
        // Pad the inverse transformation, float rounding and cell boundaries generously.
        // Extreme/modded coordinates and long trajectories use the native full scan.
        var magnitude = Math.Max(Math.Abs(origin.x), Math.Max(Math.Abs(origin.y), Math.Abs(origin.z)));
        var padding = Math.Max(65536.0, magnitude * 0.00001);
        if (double.IsNaN(magnitude) || double.IsInfinity(magnitude) || padding > Cell / 4 ||
            double.IsNaN(distance) || double.IsInfinity(distance) || distance < 0 || distance > 1000000) return false;
        var end = origin + direction * distance;
        const double radius = 3600000;
        var low = Key(new VectorLF3(Math.Min(origin.x, end.x) - radius - padding,
            Math.Min(origin.y, end.y) - radius - padding, Math.Min(origin.z, end.z) - radius - padding));
        var high = Key(new VectorLF3(Math.Max(origin.x, end.x) + radius + padding,
            Math.Max(origin.y, end.y) + radius + padding, Math.Max(origin.z, end.z) + radius + padding));
        for (var x = low.Item1; x <= high.Item1; x++)
            for (var y = low.Item2; y <= high.Item2; y++)
                for (var z = low.Item3; z <= high.Item3; z++)
                    if (cells.TryGetValue((x, y, z), out var ids)) result.AddRange(ids);
        result.Sort();
        return true;
    }
}

internal static class SpaceRayCandidates
{
    private sealed class HiveSet { internal readonly List<int> Ids = new(); internal AstroPoseR[] Source; }
    private sealed class QueryState
    {
        internal readonly List<int> Stars = new();
        internal List<int> Hives;
        internal int StarCursor, HiveCursor;
        internal bool Indexed;
    }
    private static readonly ConditionalWeakTable<GalaxyData, GalaxySpatialIndex> grids = new();
    private static readonly ConditionalWeakTable<SectorModel, HiveSet> hives = new();
    private static readonly ConditionalWeakTable<SpaceColliderLogic, QueryState> queries = new();

    internal static void Observe(SectorModel model)
    {
        if (!GalaxyOptimization.Enabled || model.gameData.galaxy.starCount < GalaxyWork.ParallelThreshold) return;
        var set = hives.GetOrCreateValue(model);
        set.Ids.Clear(); set.Source = model.astroArr;
        var end = Math.Min(model.sector.maxHiveCount, model.sector.astroCursor);
        for (var i = 1; i < end; i++) if (model.astroArr[i].radius > 1f) set.Ids.Add(i);
    }

    internal static bool Prepare(SpaceColliderLogic logic, Ray ray, float distance)
    {
        var state = queries.GetOrCreateValue(logic);
        state.Indexed = false; state.Hives = null; state.StarCursor = state.HiveCursor = 0;
        if (!GalaxyOptimization.Enabled || logic.gameData.galaxy.starCount < GalaxyWork.ParallelThreshold) return false;
        var model = logic.sector.model;
        if (hives.TryGetValue(model, out var set) && ReferenceEquals(set.Source, model.astroArr)) state.Hives = set.Ids;
        var rotation = logic.gameData.relativeRot;
        var norm = (double)rotation.x * rotation.x + (double)rotation.y * rotation.y +
            (double)rotation.z * rotation.z + (double)rotation.w * rotation.w;
        if (Math.Abs(norm - 1.0) > 0.00001 || double.IsNaN(norm)) return false;
        var origin = logic.gameData.relativePos + Maths.QRotateLF(rotation, (VectorLF3)ray.origin);
        var direction = Maths.QRotateLF(rotation, (VectorLF3)ray.direction);
        state.Indexed = grids.GetValue(logic.gameData.galaxy, galaxy => new GalaxySpatialIndex(galaxy))
            .Query(origin, direction, distance, state.Stars);
        return state.Indexed;
    }

    internal static int NextHive(int previous, SpaceColliderLogic logic)
    {
        if (!GalaxyOptimization.Enabled || !queries.TryGetValue(logic, out var state) || state.Hives == null) return previous + 1;
        return state.HiveCursor < state.Hives.Count ? state.Hives[state.HiveCursor++] : logic.sector.maxHiveCount;
    }
    internal static int NextStar(int previous, SpaceColliderLogic logic)
    {
        if (!GalaxyOptimization.Enabled || !queries.TryGetValue(logic, out var state) || !state.Indexed) return previous + 1;
        return state.StarCursor < state.Stars.Count ? state.Stars[state.StarCursor++] : logic.gameData.galaxy.starCount + 1;
    }
}

[HarmonyPatch(typeof(SpaceColliderLogic), nameof(SpaceColliderLogic.RaycastAllColliders))]
internal static class SpaceRayCandidates_Patch
{
    [HarmonyPrefix]
    internal static bool Prefix(SpaceColliderLogic __instance, Ray relative_ray, float distance, float ray_area, bool cast_physics)
    {
        if (GalaxyOptimization.Enabled && __instance.gameData.galaxy.starCount >= GalaxyWork.ParallelThreshold &&
            SpaceRayCandidates.Prepare(__instance, relative_ray, distance)) return true;
        // No helper calls inside native loops for small worlds, disabled mode or fallback queries.
        Native(__instance, relative_ray, distance, ray_area, cast_physics);
        return false;
    }

    [HarmonyReversePatch(HarmonyReversePatchType.Original)]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Native(SpaceColliderLogic instance, Ray relative_ray, float distance, float ray_area, bool cast_physics)
        => throw new NotSupportedException("Native ray fallback was not installed.");

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        // Checked against the supported binary. Only replace the two induction variables;
        // all sphere tests, hit sorting, dynamic astros, meshes and enemy tests remain native.
        RewriteLoop(code, 5, nameof(SpaceRayCandidates.NextHive));
        RewriteLoop(code, 11, nameof(SpaceRayCandidates.NextStar));
        return code;
    }

    private static void RewriteLoop(List<CodeInstruction> code, int local, string helper)
    {
        var stores = code.Select((instruction, index) => (instruction, index))
            .Where(item => item.instruction.IsStloc() && Local(item.instruction) == local).Select(item => item.index).ToArray();
        if (stores.Length != 2 || !code[stores[0] - 1].LoadsConstant(1) || code[stores[1] - 1].opcode != OpCodes.Add ||
            !code[stores[1] - 2].LoadsConstant(1) || !code[stores[1] - 3].IsLdloc() || Local(code[stores[1] - 3]) != local)
            throw new InvalidOperationException("Space ray induction variable changed: " + local);
        var call = AccessTools.Method(typeof(SpaceRayCandidates), helper);
        var increment = stores[1];
        code[increment - 2].opcode = OpCodes.Ldarg_0; code[increment - 2].operand = null;
        code[increment - 1].opcode = OpCodes.Call; code[increment - 1].operand = call;
        var init = stores[0] - 1;
        code[init].opcode = OpCodes.Ldc_I4_0;
        code.InsertRange(init + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, call) });
    }
    private static int Local(CodeInstruction instruction) => instruction.operand is LocalBuilder builder ? builder.LocalIndex :
        instruction.opcode == OpCodes.Ldloc_0 || instruction.opcode == OpCodes.Stloc_0 ? 0 :
        instruction.opcode == OpCodes.Ldloc_1 || instruction.opcode == OpCodes.Stloc_1 ? 1 :
        instruction.opcode == OpCodes.Ldloc_2 || instruction.opcode == OpCodes.Stloc_2 ? 2 :
        instruction.opcode == OpCodes.Ldloc_3 || instruction.opcode == OpCodes.Stloc_3 ? 3 : Convert.ToInt32(instruction.operand);
}
