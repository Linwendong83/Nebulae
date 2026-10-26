using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic;

// Canonical poses remain per tick. Only distant stars' Unity presentation waits for Update.
[HarmonyPatch(typeof(UniverseSimulator), nameof(UniverseSimulator.GameTick))]
internal static class StarDisplayCadence_Patch
{
    private static UniverseSimulator pending;
    private static Vector3 playerPosition, cameraPosition;
    private static VectorLF3 playerUniversal;
    private static Quaternion cameraRotation;
    public static long QueuedBatches, RenderedBatches;

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.Method(typeof(StarSimulator), nameof(StarSimulator.UpdateUniversalPosition));
        var replacement = AccessTools.Method(typeof(StarDisplayCadence_Patch), nameof(Update));
        var count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original)) { instruction.opcode = OpCodes.Call; instruction.operand = replacement; count++; }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Universe star display loop changed: " + count);
    }

    internal static void Update(StarSimulator star, Vector3 local, VectorLF3 universal, Vector3 camera, Quaternion rotation)
    {
        var universe = GameMain.universeSimulator;
        if (!Multiplayer.IsActive || Multiplayer.IsDedicated || GameMain.inOtherScene ||
            GameMain.data?.guideMission != null || star.starData == null || universe?.starSimulators == null ||
            universe.starSimulators.Length < GalaxyWork.ParallelThreshold)
        {
            Flush();
            star.UpdateUniversalPosition(local, universal, camera, rotation);
            return;
        }
        // Local lighting, planet shaders, sound and minimaps may consume this before rendering.
        if (star.starData == GameMain.localStar)
        {
            star.UpdateUniversalPosition(local, universal, camera, rotation);
            return;
        }
        if (star.starData.index == 0 || (GameMain.localStar?.index == 0 && star.starData.index == 1))
        {
            pending = universe; playerPosition = local; playerUniversal = universal;
            cameraPosition = camera; cameraRotation = rotation; QueuedBatches++;
        }
    }

    internal static void Flush()
    {
        var universe = pending;
        pending = null;
        if (universe == null || universe != GameMain.universeSimulator || Multiplayer.IsDedicated) return;
        VisualWrites.BeginFrame();
        try
        {
            foreach (var star in universe.starSimulators)
                if (star != null && star.starData != GameMain.localStar)
                    star.UpdateUniversalPosition(playerPosition, playerUniversal, cameraPosition, cameraRotation);
            RenderedBatches++;
        }
        finally { VisualWrites.EndFrame(); }
    }

    [HarmonyPrefix, HarmonyPatch(typeof(GameMain), "Update")]
    internal static void BeforeUpdate() => Flush();

    [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyPatch(typeof(StarSimulator), "LateUpdate")]
    internal static void BeforeStarLateUpdate() => Flush();

    [HarmonyPrefix, HarmonyPatch(typeof(UniverseSimulator), nameof(UniverseSimulator.OnGameEnd))]
    internal static void Clear() => pending = null;
}
