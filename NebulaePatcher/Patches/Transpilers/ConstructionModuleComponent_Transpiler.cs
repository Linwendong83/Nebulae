using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Transpilers;

/// <summary>Report every completed material handoff, including base storage, after its native debit.</summary>
[HarmonyPatch(typeof(ConstructionModuleComponent))]
internal class ConstructionModuleComponent_Transpiler
{
    [ThreadStatic] private static int lastInc;
    [HarmonyTranspiler, HarmonyPriority(Priority.High)]
    [HarmonyPatch(nameof(ConstructionModuleComponent.PlaceItems))]
    public static IEnumerable<CodeInstruction> PlaceItems_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var field = AccessTools.Field(typeof(PrebuildData), nameof(PrebuildData.itemRequired));
        var matches = new[] { new CodeMatch(i => i.IsLdloc()), new CodeMatch(OpCodes.Ldflda, field),
            new CodeMatch(OpCodes.Dup), new CodeMatch(OpCodes.Ldind_I4), new CodeMatch(i => i.IsLdloc()),
            new CodeMatch(OpCodes.Sub), new CodeMatch(OpCodes.Stind_I4) };
        if (!AuthorityTranspilerGuard.VerifyCount("ConstructionModuleComponent.PlaceItems.itemRequired", codes, 2, true, matches)) return codes;
        var take = AccessTools.Method(typeof(StorageComponent), nameof(StorageComponent.TakeTailItems),
            new[] { typeof(int).MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType(), typeof(bool) });
        var result = new List<CodeInstruction>();
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            if (code.Calls(take))
            {
                result.Add(new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(code));
                code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(ConstructionModuleComponent_Transpiler), nameof(TakeItems));
            }
            result.Add(code);
            if (i < 6 || code.opcode != OpCodes.Stind_I4 || codes[i - 5].opcode != OpCodes.Ldflda || !Equals(codes[i - 5].operand, field)) continue;
            result.Add(new CodeInstruction(OpCodes.Ldarg_1));
            result.Add(new CodeInstruction(codes[i - 6].opcode, codes[i - 6].operand));
            result.Add(new CodeInstruction(codes[i - 2].opcode, codes[i - 2].operand));
            result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ConstructionModuleComponent_Transpiler), nameof(Report))));
        }
        return result;
    }
    private static void TakeItems(StorageComponent storage, ref int item, ref int count, out int inc, bool useBan, PlanetFactory factory)
    {
        storage.TakeTailItems(ref item, ref count, out inc, useBan); lastInc = inc;
        if (Multiplayer.IsActive && Multiplayer.Session.IsServer && storage.entityId > 0 && count > 0)
            Multiplayer.Session.BuildDispatch.StorageSupplied(factory, storage);
    }
    private static void Report(PlanetFactory factory, ref PrebuildData site, int count)
    {
        if (Multiplayer.IsActive && count > 0 && site.itemRequired == 0)
            Multiplayer.Session.BuildDispatch.ReportSupply(factory, site, count, lastInc);
    }
}
