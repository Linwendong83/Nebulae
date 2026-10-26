using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System;
using System.Threading;
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
            if (code.opcode == OpCodes.Ldfld && code.operand is FieldInfo ammoField && ammoField.DeclaringType == typeof(Mecha))
            {
                var getter = ammoField.Name == nameof(Mecha.ammoItemId) ? nameof(PlayerWeaponResources.DisplayAmmoItem) :
                    ammoField.Name == nameof(Mecha.ammoBulletCount) ? nameof(PlayerWeaponResources.DisplayAmmoBullets) :
                    ammoField.Name == nameof(Mecha.ammoInc) ? nameof(PlayerWeaponResources.DisplayAmmoInc) : null;
                if (getter != null)
                {
                    code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(PlayerWeaponResources), getter);
                    yield return code;
                    continue;
                }
            }
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
internal static class NativeCraftEnergy_Patch
{
    [ThreadStatic] private static HashSet<Mecha> active;
    internal struct State
    {
        internal Mecha Mecha;
        internal double Core, Budget;
    }
    // Preserve the native field accesses: inserting helper calls into the space
    // engagement methods breaks their by-ref movement math in Unity Mono. Borrow
    // only the predicted client budget around the original body, then restore it.
    internal static void Before(Mecha mecha, out State __state)
    {
        __state = default;
        if (!WorldSimulation.IsClient || mecha == null || active?.Contains(mecha) == true) return;
        Monitor.Enter(mecha);
        try
        {
            var budget = Core(mecha);
            __state = new State { Mecha = mecha, Core = mecha.coreEnergy, Budget = budget };
            (active ??= new HashSet<Mecha>()).Add(mecha); mecha.coreEnergy = budget;
        }
        catch { Monitor.Exit(mecha); throw; }
    }
    internal static Exception After(State __state, Exception __exception)
    {
        var mecha = __state.Mecha;
        if (mecha == null) return __exception;
        try
        {
            var spent = Math.Max(0, __state.Budget - mecha.coreEnergy);
            mecha.coreEnergy = __state.Core; active.Remove(mecha);
            Multiplayer.Session.PlayerWeapons.Resources.Recovery.Spend(mecha, 0, 0, spent);
        }
        finally { mecha.coreEnergy = __state.Core; active.Remove(mecha); Monitor.Exit(mecha); }
        return __exception;
    }
    internal static bool Accounting(Mecha mecha) => active?.Contains(mecha) != true;
    internal static double Core(Mecha m) => WorldSimulation.IsClient ? Multiplayer.Session.PlayerWeapons.Resources.Recovery.Get(m).coreEnergy : m.coreEnergy;
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

[HarmonyPatch]
internal static class NativeCraftEnergyBatch_Patch
{
    // The large native ship methods must have neither injected field accesses nor
    // exception wrappers: both disturb Mono's by-ref movement locals. The existing
    // simulation boundary provides one scope for every mecha referenced in the batch.
    private static IEnumerable<MethodBase> TargetMethods() => new[]
    {
        AccessTools.Method(typeof(CombatSpaceSystem), nameof(CombatSpaceSystem.GameTick)),
        AccessTools.Method(typeof(CombatGroundSystem), nameof(CombatGroundSystem.GameTick))
    };
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static void Before(out NativeCraftEnergy_Patch.State[] __state)
    {
        __state = null;
        if (!WorldSimulation.IsClient || Multiplayer.Session?.Combat?.Players == null) return;
        var mechas = new[] { GameMain.mainPlayer?.mecha }.Concat(Multiplayer.Session.Combat.Players.Select(p => p.mecha))
            .Where(m => m != null).Distinct().ToArray();
        __state = new NativeCraftEnergy_Patch.State[mechas.Length];
        try { for (var i = 0; i < mechas.Length; i++) NativeCraftEnergy_Patch.Before(mechas[i], out __state[i]); }
        catch { After(__state, null); throw; }
    }
    [HarmonyFinalizer]
    private static Exception After(NativeCraftEnergy_Patch.State[] __state, Exception __exception)
    {
        if (__state == null) return __exception;
        for (var i = __state.Length - 1; i >= 0; i--)
            try { NativeCraftEnergy_Patch.After(__state[i], null); }
            catch (Exception error) { __exception ??= error; }
        return __exception;
    }
}

[HarmonyPatch(typeof(Mecha), nameof(Mecha.MarkEnergyChange))]
internal static class NativeCraftEnergyAccounting_Patch
{
    [HarmonyPrefix] private static bool Before(Mecha __instance) => NativeCraftEnergy_Patch.Accounting(__instance);
}

[HarmonyPatch]
internal static class NativeBaseEnergy_Patch
{
    private static IEnumerable<MethodBase> TargetMethods() => AccessTools.GetDeclaredMethods(typeof(UnitComponent))
        .Where(m => m.GetMethodBody() != null && PatchProcessor.GetOriginalInstructions(m).Any(code =>
            code.LoadsField(AccessTools.Field(typeof(BattleBaseComponent), nameof(BattleBaseComponent.energy))) ||
            code.StoresField(AccessTools.Field(typeof(BattleBaseComponent), nameof(BattleBaseComponent.energy)))));
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var field = AccessTools.Field(typeof(BattleBaseComponent), nameof(BattleBaseComponent.energy));
        foreach (var code in instructions)
        {
            var name = code.LoadsField(field) ? nameof(NativeCraftEnergy_Patch.BaseEnergy) : code.StoresField(field) ? nameof(NativeCraftEnergy_Patch.SetBaseEnergy) : null;
            if (name != null) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(NativeCraftEnergy_Patch), name); }
            yield return code;
        }
    }
}
