using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Player;
using UnityEngine;

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch]
internal static class TerrainVegetationQuery_Transpiler
{
    private static readonly AccessTools.FieldRef<PlanetFactory, int[]> Buffer = AccessTools.FieldRefAccess<PlanetFactory, int[]>("tmp_ids");
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.FlattenTerrain));
        yield return AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.FlattenTerrainReform));
    }
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var query = AccessTools.Method(typeof(NearColliderLogic), nameof(NearColliderLogic.GetVegetablesInAreaNonAlloc));
        var replace = AccessTools.Method(typeof(TerrainVegetationQuery_Transpiler), nameof(Collect));
        var count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(query))
            {
                var factory = new CodeInstruction(OpCodes.Ldarg_0);
                factory.labels.AddRange(instruction.labels); instruction.labels.Clear();
                yield return factory;
                instruction.opcode = OpCodes.Call; instruction.operand = replace; count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new System.InvalidOperationException("Native terrain vegetation query changed.");
    }
    private static ColliderData[] Colliders(short proto) => LDB.veges.Select(proto)?.prefabDesc?.colliders;
    public static int Collect(NearColliderLogic native, Vector3 center, float radius, int[] ids, PlanetFactory factory)
    {
        if (!Multiplayer.IsActive || factory?.vegePool == null ||
            Multiplayer.Session.Vegetation?.IsBulkAction != true && factory.planet.factoryLoaded)
            return native.GetVegetablesInAreaNonAlloc(center, radius, ids);
        var original = ids;
        var count = TerrainVegetationQuery.Collect(factory.vegePool, factory.vegeCursor, center, radius, ref ids, Colliders);
        if (!ReferenceEquals(original, ids)) Buffer(factory) = ids;
        return count;
    }
}
