using System.Runtime.CompilerServices;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic.Combat;

// PredictPose already computes tick + 1. Reuse exactly those bits on the next
// consecutive call, retaining native arithmetic and sequential hive execution.
[HarmonyPatch(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.GameTickLogic))]
internal static class HiveOrbitCache_Patch
{
    internal static bool Enabled = true;
    internal static bool DirectIndexEnabled = true;
    [HarmonyPrepare]
    internal static bool Prepare() => Multiplayer.IsDedicated;
    internal sealed class Frame
    {
        internal readonly Dictionary<AstroOrbitData, State> ByOrbit = new();
        internal readonly State[] ByAstro;
        internal Frame(SpaceSector sector) => ByAstro = new State[Math.Max(1, sector.maxHiveCount)];
    }
    private static readonly ConditionalWeakTable<SpaceSector, Frame> sectorStates = new();
    [ThreadStatic] private static Frame current;

    internal static Frame Enter(SpaceSector sector)
    {
        var previous = current;
        current = Enabled && sector.galaxy?.starCount >= GalaxyWork.ParallelThreshold ? sectorStates.GetValue(sector, s => new Frame(s)) : null;
        return previous;
    }
    internal static void Exit(Frame previous) => current = previous;

    internal sealed class State
    {
        private bool initialized;
        private long tick;
        private float radius, phase;
        private double period;
        private Quaternion rotation;
        private VectorLF3 normal, center, nextPosition;
        private Quaternion nextRotation;

        internal bool Matches(AstroOrbitData orbit, long time, VectorLF3 origin) => initialized &&
            tick != long.MaxValue && time == tick + 1 &&
            PoseBits.Equal(radius, orbit.orbitRadius) &&
            PoseBits.Equal(phase, orbit.orbitPhase) &&
            PoseBits.Equal(period, orbit.orbitalPeriod) &&
            PoseBits.Equal(rotation, orbit.orbitRotation) &&
            PoseBits.Equal(normal, orbit.orbitNormal) &&
            PoseBits.Equal(center, origin);

        internal void Reuse(ref AstroData astro)
        {
            astro.uPos = nextPosition;
            astro.uRot = nextRotation;
        }

        internal void Capture(AstroOrbitData orbit, long time, VectorLF3 origin, in AstroData astro)
        {
            tick = time; radius = orbit.orbitRadius; phase = orbit.orbitPhase;
            period = orbit.orbitalPeriod; rotation = orbit.orbitRotation;
            normal = orbit.orbitNormal; center = origin;
            nextPosition = astro.uPosNext; nextRotation = astro.uRotNext;
            initialized = true;
        }
    }

    internal static void Predict(AstroOrbitData orbit, long time, VectorLF3 center, ref AstroData astroData)
    {
        if (!Enabled || current == null)
        {
            orbit.PredictPose(time, center, ref astroData);
            return;
        }
        State state;
        var index = astroData.id - 1000000;
        if (DirectIndexEnabled && (uint)index < (uint)current.ByAstro.Length)
            state = current.ByAstro[index] ??= new State();
        else if (!current.ByOrbit.TryGetValue(orbit, out state)) current.ByOrbit.Add(orbit, state = new State());
        if (state.Matches(orbit, time, center)) ReuseAndAdvance(orbit, time, center, ref astroData, state);
        else orbit.PredictPose(time, center, ref astroData);
        state.Capture(orbit, time, center, astroData);
    }

    private static void ReuseAndAdvance(AstroOrbitData orbit, long time, VectorLF3 center, ref AstroData astroData, State state)
    {
        var phase = (double)time / (orbit.orbitalPeriod * 60.0) + (double)orbit.orbitPhase / 360.0;
        var whole = (int)(phase + 0.1);
        phase -= (double)whole;
        orbit.runtimeOrbitPhase = (float)phase * 360f;
        state.Reuse(ref astroData);

        var radius = 40000.0 * (double)orbit.orbitRadius;
        var nextPhase = (double)(time + 1) / (orbit.orbitalPeriod * 60.0) + (double)orbit.orbitPhase / 360.0;
        var nextWhole = (int)(nextPhase + 0.1);
        nextPhase -= (double)nextWhole;
        nextPhase *= System.Math.PI * 2.0;
        astroData.uPosNext = Maths.QRotateLF(orbit.orbitRotation,
            new VectorLF3((float)System.Math.Cos(nextPhase), 0f, (float)System.Math.Sin(nextPhase)));
        astroData.uRotNext = Quaternion.LookRotation(orbit.orbitNormal, astroData.uPosNext);
        astroData.uPosNext.x = astroData.uPosNext.x * radius + center.x;
        astroData.uPosNext.y = astroData.uPosNext.y * radius + center.y;
        astroData.uPosNext.z = astroData.uPosNext.z * radius + center.z;
    }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var native = AccessTools.Method(typeof(AstroOrbitData), nameof(AstroOrbitData.PredictPose));
        var cached = AccessTools.Method(typeof(HiveOrbitCache_Patch), nameof(Predict));
        var count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(native)) { instruction.opcode = OpCodes.Call; instruction.operand = cached; count++; }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Hive orbital prediction call changed: " + count);
    }
}

// One weak-table lookup per sector tick rather than per hive. The finalizer also
// restores the scope on native exceptions and nested simulation calls.
[HarmonyPatch(typeof(SpaceSector), nameof(SpaceSector.GameTick))]
internal static class HiveOrbitScope_Patch
{
    [HarmonyPrepare]
    internal static bool Prepare() => Multiplayer.IsDedicated;
    [HarmonyPrefix]
    internal static void Prefix(SpaceSector __instance, out HiveOrbitCache_Patch.Frame __state)
        => __state = HiveOrbitCache_Patch.Enter(__instance);
    [HarmonyFinalizer]
    internal static void Finalizer(HiveOrbitCache_Patch.Frame __state)
        => HiveOrbitCache_Patch.Exit(__state);
}
