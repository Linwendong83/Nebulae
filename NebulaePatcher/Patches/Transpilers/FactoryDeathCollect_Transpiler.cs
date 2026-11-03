using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Transpilers;

/// <summary>Skips the full collection the game forces after dismantling a container in multiplayer.</summary>
/// <remarks>
/// <para>
/// <c>PlanetFactory.TakeBackItemsInEntity</c> runs <c>GC.Collect()</c> after a storage, station or
/// dispenser is emptied. Every peer replays the same dismantle, so one storage box becomes one
/// blocking full collection per peer, and an area dismantle multiplies that by the number of
/// containers. The managed heap does not need the hint: the temporary grids are collected by the
/// next natural collection, and a multiplayer frame must not stop for it.
/// </para>
/// <para>
/// Singleplayer keeps the vanilla behaviour, so this only changes the multiplayer path.
/// </para>
/// </remarks>
[HarmonyPatch(typeof(PlanetFactory))]
internal static class FactoryDeathCollect_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(PlanetFactory.TakeBackItemsInEntity))]
    private static IEnumerable<CodeInstruction> TakeBackItemsInEntity_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var collect = AccessTools.Method(typeof(GC), "Collect", Type.EmptyTypes);
        var replacement = AccessTools.Method(typeof(FactoryDeathCollect_Transpiler), nameof(CollectOnlyInSingleplayer));
        var replaced = 0;
        foreach (var instruction in instructions)
        {
            if (collect != null && instruction.opcode == OpCodes.Call && Equals(instruction.operand, collect))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replaced++;
            }
            yield return instruction;
        }
        if (replaced == 0)
            Log.Error("FactoryDeathCollect_Transpiler found no GC.Collect call in TakeBackItemsInEntity.");
    }

    private static void CollectOnlyInSingleplayer()
    {
        if (!Multiplayer.IsActive) GC.Collect();
    }
}
