using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.SensorLogic_Ground))]
internal static class GroundEnemySensor_Patch
{
    [HarmonyPrefix]
    private static void Prefix(ref EnemyData enemy, PlanetFactory factory, out EnemySensorContext.State __state)
        => __state = EnemySensorContext.Begin(factory, enemy);
    [HarmonyFinalizer]
    private static Exception Finalizer(EnemySensorContext.State __state, Exception __exception)
    { EnemySensorContext.Restore(__state); return __exception; }
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        => EnemySensorAdapter.Transform(instructions, generator, false);
}

[HarmonyPatch(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.SensorLogic_Space))]
internal static class SpaceEnemySensor_Patch
{
    [HarmonyPrefix]
    private static void Prefix(ref EnemyData enemy, SpaceSector sector, EnemyDFHiveSystem hive, out EnemySensorContext.State __state)
        => __state = EnemySensorContext.Begin(sector, hive, enemy);
    [HarmonyFinalizer]
    private static Exception Finalizer(EnemySensorContext.State __state, Exception __exception)
    { EnemySensorContext.Restore(__state); return __exception; }
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        => EnemySensorAdapter.Transform(instructions, generator, true);
}

internal static class EnemySensorAdapter
{
    public static IEnumerable<CodeInstruction> Transform(IEnumerable<CodeInstruction> instructions, ILGenerator generator, bool space)
    {
        var code = new List<CodeInstruction>(instructions);
        var fields = new Dictionary<System.Reflection.FieldInfo, string>
        {
            [AccessTools.Field(typeof(EnemyDFGroundSystem), "local_player_pos")] = nameof(EnemySensorContext.GroundPosition),
            [AccessTools.Field(typeof(EnemyDFGroundSystem), "local_player_exist_alive")] = nameof(EnemySensorContext.GroundAlive),
            [AccessTools.Field(typeof(EnemyDFGroundSystem), "local_player_grounded_alive")] = nameof(EnemySensorContext.Grounded),
            [AccessTools.Field(typeof(EnemyDFHiveSystem), "local_player_exist_alive")] = nameof(EnemySensorContext.SpaceAlive),
            [AccessTools.Field(typeof(EnemyDFHiveSystem), "player_local_pos")] = nameof(EnemySensorContext.HivePosition),
            [AccessTools.Field(typeof(Player), nameof(Player.uPosition))] = nameof(EnemySensorContext.UniversalPosition),
            [AccessTools.Field(typeof(SkillSystem), nameof(SkillSystem.playerEnergyShieldRadius))] = nameof(EnemySensorContext.ShieldRadius)
        };
        var ids = 0; var positions = 0;
        for (var i = 0; i < code.Count; i++)
        {
            if (code[i].operand is System.Reflection.FieldInfo field && fields.TryGetValue(field, out var getter) &&
                (code[i].opcode == OpCodes.Ldfld || code[i].opcode == OpCodes.Ldflda))
            {
                var readValue = code[i].opcode == OpCodes.Ldfld;
                var method = AccessTools.Method(typeof(EnemySensorContext), getter);
                code[i].opcode = OpCodes.Call; code[i].operand = method;
                if (readValue && method.ReturnType.IsByRef) code.Insert(++i, new CodeInstruction(OpCodes.Ldobj, field.FieldType));
                positions++;
            }
            if (code[i].LoadsConstant(1006632961))
            { code[i].opcode = OpCodes.Call; code[i].operand = AccessTools.Method(typeof(EnemySensorContext), nameof(EnemySensorContext.PlayerTarget)); }
            if (i + 1 < code.Count && code[i].LoadsConstant((int)ETargetType.Player) && code[i + 1].LoadsConstant(1))
            { code[i + 1].opcode = OpCodes.Call; code[i + 1].operand = AccessTools.Method(typeof(EnemySensorContext), nameof(EnemySensorContext.PlayerId)); ids++; }
            if (!space) continue;
            foreach (var property in new[] { nameof(GameMain.localStar), nameof(GameMain.localPlanet), nameof(GameMain.mainPlayer) })
            {
                if (!code[i].Calls(AccessTools.PropertyGetter(typeof(GameMain), property))) continue;
                var name = property == nameof(GameMain.localStar) ? nameof(EnemySensorContext.LocalStar) :
                    property == nameof(GameMain.localPlanet) ? nameof(EnemySensorContext.LocalPlanet) : nameof(EnemySensorContext.MainPlayer);
                code[i].operand = AccessTools.Method(typeof(EnemySensorContext), name);
            }
            if (code[i].Calls(AccessTools.PropertyGetter(typeof(Player), nameof(Player.fastTravelling))))
            { code[i].opcode = OpCodes.Call; code[i].operand = AccessTools.Method(typeof(EnemySensorContext), nameof(EnemySensorContext.FastTravelling)); }
        }
        if (ids == 0 || positions < 2) throw new InvalidOperationException("Native enemy sensor inputs changed.");
        if (space)
        {
            var player = code.FindIndex(x => x.Calls(AccessTools.Method(typeof(EnemySensorContext), nameof(EnemySensorContext.MainPlayer))));
            if (player < 0) throw new InvalidOperationException("Native space sensor player block is missing.");
            var end = generator.DefineLabel();
            code[code.Count - 1].labels.Add(end);
            var hasPlayer = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EnemySensorContext), nameof(EnemySensorContext.HasPlayer)));
            hasPlayer.labels.AddRange(code[player].labels); code[player].labels.Clear();
            code.InsertRange(player, new[] { hasPlayer, new CodeInstruction(OpCodes.Brfalse, end) });
        }
        return code;
    }
}
