using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch(typeof(PlayerAction_Inspect), nameof(PlayerAction_Inspect.GameTick))]
internal static class ManualBuild_Transpiler
{
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.ToList();
        var builds = codes.Select((code, i) => (code, i)).Where(x => x.code.Calls(AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.BuildFinally)))).ToArray();
        if (builds.Length != 1) throw new InvalidOperationException("Manual construction completion changed.");
        var build = builds[0].i;
        var take = codes.FindLastIndex(build, c => c.operand is MethodInfo method && method.DeclaringType == typeof(StorageComponent) && method.Name == "TakeTailItems");
        if (take < 0 || build - take > 30) throw new InvalidOperationException("Manual construction material reservation changed.");
        codes[take].opcode = codes[build].opcode = OpCodes.Call;
        codes[take].operand = AccessTools.Method(typeof(ManualBuild_Transpiler), nameof(Take));
        codes[build].operand = AccessTools.Method(typeof(ManualBuild_Transpiler), nameof(Build));
        return codes;
    }
    private static void Take(StorageComponent package, ref int itemId, ref int count, out int inc, bool useBan)
    {
        inc = 0;
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) package.TakeTailItems(ref itemId, ref count, out inc, useBan);
    }
    private static void Build(PlanetFactory factory, Player player, int id, bool autoRefresh, bool flattenTerrain)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient) Multiplayer.Session.ManualBuilds.Request(factory, id);
        else factory.BuildFinally(player, id, autoRefresh, flattenTerrain);
    }
}
