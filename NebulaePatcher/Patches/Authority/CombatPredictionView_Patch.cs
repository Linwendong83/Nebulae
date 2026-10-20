using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class CombatPredictionView_Patch
{
    private static readonly HashSet<string> fields = new()
    {
        "hp", "hpRecoverCD", "coreEnergy", "energyShieldEnergy", "energyShieldRecoverCD", "energyShieldRechargeCurrent",
        "energyShieldResistHistory", "laserEnergy", "laserRecharging"
    };
    private static IEnumerable<MethodBase> TargetMethods() => new[] { typeof(UIMechaEnergy), typeof(UIMechaMoveTip), typeof(UIMechaWindow),
        typeof(UIZS_MechaStatBars), typeof(UIZS_WeaponPanel), typeof(PlayerEffect) }.SelectMany(AccessTools.GetDeclaredMethods).Where(x => x.GetMethodBody() != null);
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var code in instructions)
        {
            var field = code.opcode == OpCodes.Ldfld && code.operand is FieldInfo f && f.DeclaringType == typeof(Mecha) && fields.Contains(f.Name);
            var property = (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) && code.operand is MethodInfo m &&
                m.DeclaringType == typeof(Mecha) && m.Name.StartsWith("get_") &&
                (m.Name.Contains("energyShield") || m.Name.Contains("laserEnergy") || m.Name.Contains("hpPercentage"));
            if (field || property)
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CombatRecoveryView), nameof(CombatRecoveryView.Display))).MoveLabelsFrom(code).MoveBlocksFrom(code);
            yield return code;
        }
    }
}

/// <summary>Native craft movement/fire can spend only its client budget, never a second real debit.</summary>
[HarmonyPatch]
internal static class NativeCraftEnergy_Patch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var core = AccessTools.Field(typeof(Mecha), nameof(Mecha.coreEnergy));
        var energy = AccessTools.Field(typeof(BattleBaseComponent), nameof(BattleBaseComponent.energy));
        var mark = AccessTools.Method(typeof(Mecha), nameof(Mecha.MarkEnergyChange));
        // Leave math/serialization methods native. In particular, detouring the private struct
        // RotateVector overloads can corrupt the fleet's fields in Unity Mono's optimized callers.
        return new[] { typeof(UnitComponent), typeof(FleetComponent) }.SelectMany(AccessTools.GetDeclaredMethods)
            .Where(m => m.GetMethodBody() != null && PatchProcessor.GetOriginalInstructions(m).Any(code =>
                code.LoadsField(core) || code.StoresField(core) || code.LoadsField(energy) || code.StoresField(energy) || code.Calls(mark)));
    }
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var core = AccessTools.Field(typeof(Mecha), nameof(Mecha.coreEnergy));
        var energy = AccessTools.Field(typeof(BattleBaseComponent), nameof(BattleBaseComponent.energy));
        var mark = AccessTools.Method(typeof(Mecha), nameof(Mecha.MarkEnergyChange));
        foreach (var code in instructions)
        {
            string name = code.LoadsField(core) ? nameof(Core) : code.StoresField(core) ? nameof(SetCore) :
                code.LoadsField(energy) ? nameof(BaseEnergy) : code.StoresField(energy) ? nameof(SetBaseEnergy) : code.Calls(mark) ? nameof(Mark) : null;
            if (name != null) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(NativeCraftEnergy_Patch), name); }
            yield return code;
        }
    }
    internal static double Core(Mecha m) => WorldSimulation.IsClient ? Multiplayer.Session.PlayerWeapons.Resources.Recovery.Get(m).coreEnergy : m.coreEnergy;
    internal static void SetCore(Mecha m, double value)
    {
        if (!WorldSimulation.IsClient) { m.coreEnergy = value; return; }
        Multiplayer.Session.PlayerWeapons.Resources.Recovery.Spend(m, 0, 0, Core(m) - value);
    }
    private static void Mark(Mecha m, int type, double amount) { if (!WorldSimulation.IsClient) m.MarkEnergyChange(type, amount); }
    // Base energy is an authority snapshot. Native client firing must not alter it.
    private sealed class Budget { public long Baseline, Value, Tick; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BattleBaseComponent, Budget> budgets = new();
    private static Budget GetBudget(BattleBaseComponent b)
    {
        var budget = budgets.GetValue(b, _ => new Budget { Baseline = b.energy, Value = b.energy, Tick = GameMain.gameTick });
        if (budget.Baseline != b.energy) { budget.Baseline = budget.Value = b.energy; budget.Tick = GameMain.gameTick; }
        return budget;
    }
    internal static long BaseEnergy(BattleBaseComponent b) => !WorldSimulation.IsClient ? b.energy :
        GameMain.gameTick - GetBudget(b).Tick > 120 ? 0 : GetBudget(b).Value;
    internal static void SetBaseEnergy(BattleBaseComponent b, long value)
    { if (!WorldSimulation.IsClient) b.energy = value; else GetBudget(b).Value = System.Math.Max(0, value); }
}
