using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeModel.Utils;
using UnityEngine;

namespace NebulaePatcher.Patches.Misc;

// Installed only on a dedicated host. The native save array becomes the live CPU buffer;
// native slot allocation, expiry, absorption and serialization continue to own the simulation.
[SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "GPU transport adapters retain the replaced native call signatures.")]
internal static class HeadlessDysonSwarm
{
    internal static readonly AccessTools.FieldRef<DysonSwarm, DysonSail[]> Pool =
        SolarSailState.Pool;
    private static readonly AccessTools.FieldRef<DysonSwarm, DysonSail[]> Temporary =
        AccessTools.FieldRefAccess<DysonSwarm, DysonSail[]>("tmpSolarSail");

    [HarmonyPrefix, HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.Init))]
    internal static bool Init(DysonSwarm __instance)
    {
        Temporary(__instance) = new DysonSail[1];
        __instance.sailCursor = __instance.sailRecycleCursor = 0;
        __instance.bulletCursor = __instance.orbitCursor = 1;
        __instance.bulletRecycleCursor = __instance.orbitCapacity = 0;
        __instance.expiryCursor = __instance.expiryEnding = 0;
        __instance.absorbCursor = __instance.absorbEnding = 0;
        __instance.orbitColorsHSVA = Colors();
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.ResetNew))]
    internal static bool Reset(DysonSwarm __instance)
    {
        Resize(__instance, 512);
        AccessTools.Method(typeof(DysonSwarm), "SetBulletCapacity").Invoke(__instance, [128]);
        AccessTools.Method(typeof(DysonSwarm), "SetOrbitCapacity").Invoke(__instance, [32]);
        var radius = __instance.dysonSphere.defOrbitRadius;
        __instance.dysonSphere.QuerySwarmRadius(ref radius);
        __instance.NewOrbit(radius, Quaternion.identity);
        __instance.orbitColorsHSVA = Colors();
        return false;
    }

    private static Vector4[] Colors()
    {
        var colors = new Vector4[32];
        for (var i = 0; i < colors.Length; i++) colors[i] = new Vector4(0.069444f, 0.35f, 1f, 1f);
        return colors;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.SetSailCapacity))]
    internal static bool Resize(DysonSwarm __instance, int newCap)
    {
        if (newCap <= 0) throw new InvalidDataException("Invalid solar sail capacity");
        var oldCap = __instance.sailCapacity;
        Array.Resize(ref Pool(__instance), newCap);
        Array.Resize(ref __instance.sailInfos, newCap);
        __instance.sailRecycle = new int[newCap];
        __instance.sailRecycleCursor = 0;
        __instance.expiryOrder = ResizeRing(__instance.expiryOrder, oldCap, newCap,
            ref __instance.expiryCursor, ref __instance.expiryEnding);
        __instance.absorbOrder = ResizeRing(__instance.absorbOrder, oldCap, newCap,
            ref __instance.absorbCursor, ref __instance.absorbEnding);
        __instance.sailCapacity = newCap;
        return false;
    }

    private static T[] ResizeRing<T>(T[] source, int oldCap, int capacity, ref int cursor, ref int ending)
    {
        var result = new T[capacity];
        var count = source == null || oldCap == 0 ? 0 : (ending - cursor + oldCap) % oldCap;
        if (count >= capacity) throw new InvalidDataException("Solar sail queue exceeds capacity");
        for (var i = 0; i < count; i++) result[i] = source[(cursor + i) % oldCap];
        cursor = 0;
        ending = count;
        return result;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.SetOrbitColorBuffer))]
    [HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.RendererBullet))]
    internal static bool SkipRenderer() => false;

    [HarmonyPostfix, HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.Free))]
    internal static void Free(DysonSwarm __instance) => Pool(__instance) = null;

    [HarmonyPrefix, HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.Export))]
    internal static void BeforeExport(DysonSwarm __instance) => SolarSailState.Validate(__instance);

    [HarmonyPostfix, HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.Import))]
    internal static void AfterImport(DysonSwarm __instance) => SolarSailState.AfterImport(__instance);

    [HarmonyPostfix, HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.RemoveSailsByOrbit))]
    internal static void Compact(DysonSwarm __instance)
    {
        Array.Resize(ref Pool(__instance), __instance.sailCapacity);
    }

    [HarmonyTranspiler, HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.GameTick))]
    internal static IEnumerable<CodeInstruction> Tick(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var shader = AccessTools.Field(typeof(DysonSwarm), "computeShader");
        var begin = code.FindIndex(i => i.opcode == OpCodes.Ldfld && Equals(i.operand, shader)) - 1;
        var end = code.FindIndex(i => i.Calls(AccessTools.Method(typeof(DysonSwarm), nameof(DysonSwarm.Dispatch_UpdatePos))));
        if (begin < 0 || end < begin || code[begin].opcode != OpCodes.Ldarg_0)
            throw new InvalidOperationException("Cannot isolate DysonSwarm GPU update block");
        // Branches from the preceding maintenance checks must enter the surviving expiry loop.
        var labels = code.Skip(begin).Take(end - begin + 1).SelectMany(i => i.labels).ToArray();
        code.RemoveRange(begin, end - begin + 1);
        code[begin].labels.AddRange(labels);
        return code;
    }

    [HarmonyTranspiler, HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.AddSolarSail))]
    [HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.RemoveSolarSail))]
    [HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.AbsorbSail))]
    [HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.OnNodeRemove))]
    [HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.Export))]
    [HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.Import))]
    [HarmonyPatch(typeof(DysonSwarm), nameof(DysonSwarm.RemoveSailsByOrbit))]
    internal static IEnumerable<CodeInstruction> Transport(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var original in instructions)
        {
            var code = new CodeInstruction(original);
            MethodInfo replacement = null;
            var instance = false;
            if (code.opcode == OpCodes.Newobj && code.operand is ConstructorInfo constructor &&
                constructor.DeclaringType == typeof(ComputeBuffer))
                replacement = AccessTools.Method(typeof(HeadlessDysonSwarm), nameof(NoBuffer));
            if (code.operand is MethodInfo method && method.DeclaringType == typeof(ComputeBuffer))
            {
                replacement = AccessTools.Method(typeof(HeadlessDysonSwarm), method.Name switch
                {
                    nameof(ComputeBuffer.SetData) => method.GetParameters().Length == 1 ? nameof(WriteAll) : nameof(Write),
                    nameof(ComputeBuffer.GetData) => nameof(Read),
                    nameof(ComputeBuffer.Release) => nameof(Release),
                    _ => throw new InvalidOperationException("Unsupported solar sail GPU operation: " + method)
                });
                instance = method.Name != nameof(ComputeBuffer.Release);
            }
            // Native compaction drops its temporary save cache; here that cache is the live buffer.
            if (code.opcode == OpCodes.Stfld && Equals(code.operand, AccessTools.Field(typeof(DysonSwarm), "sailPoolForSave")))
                replacement = AccessTools.Method(typeof(HeadlessDysonSwarm), nameof(SetPool));
            if (replacement != null)
            {
                if (instance) yield return new CodeInstruction(OpCodes.Ldarg_0).WithLabels(code.ExtractLabels());
                code.opcode = OpCodes.Call;
                code.operand = replacement;
            }
            yield return code;
        }
    }

    private static ComputeBuffer NoBuffer(int count, int stride, ComputeBufferType type) => null;
    private static void Release(ComputeBuffer buffer) { }
    private static void SetPool(DysonSwarm swarm, DysonSail[] pool)
    {
        if (pool != null) Pool(swarm) = pool;
    }
    private static void Read(ComputeBuffer buffer, Array target, int targetIndex, int sourceIndex, int count, DysonSwarm swarm)
    {
        var source = target is DysonSail[]? (Array)Pool(swarm) : swarm.sailInfos;
        if (!ReferenceEquals(source, target)) Array.Copy(source, sourceIndex, target, targetIndex, count);
    }
    private static void Write(ComputeBuffer buffer, Array source, int sourceIndex, int targetIndex, int count, DysonSwarm swarm)
    {
        // Info arrays are already changed in place; bullet data is CPU-owned too.
        if (source is DysonSail[] && !ReferenceEquals(source, Pool(swarm)))
            Array.Copy(source, sourceIndex, Pool(swarm), targetIndex, count);
    }
    private static void WriteAll(ComputeBuffer buffer, Array source, DysonSwarm swarm) =>
        Write(buffer, source, 0, 0, source.Length, swarm);
}
