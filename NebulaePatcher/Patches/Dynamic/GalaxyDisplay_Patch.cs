using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NebulaeWorld;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic;

/// <summary>Only presentation writes are suppressed; the native arithmetic and tick stay intact.</summary>
[HarmonyPatch]
internal static class GalaxyDisplay_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(StarSimulator), "LateUpdate");
        yield return AccessTools.Method(typeof(UIStarmapStar), "_OnLateUpdate");
    }

    [HarmonyPrefix]
    internal static bool Prefix() => !Multiplayer.IsDedicated;

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        => VisualWrites.Rewrite(instructions);
}

[HarmonyPatch(typeof(UniverseSimulator), nameof(UniverseSimulator.GameTick))]
internal static class HeadlessUniverseDisplay_Patch
{
    [HarmonyPrefix]
    internal static bool Prefix(UniverseSimulator __instance, double time)
    {
        if (!Multiplayer.IsDedicated) { VisualWrites.BeginFrame(); return true; }
        // UniverseSimulator.GameTick mixes canonical planet poses with camera/Unity work.
        __instance.galaxyData.UpdatePoses(time);
        return false;
    }

    [HarmonyFinalizer]
    internal static void Finalizer() => VisualWrites.EndFrame();
}

internal static class VisualWrites
{
    private sealed class MaterialValues
    {
        public int Queue = int.MinValue;
    }
    private static ConditionalWeakTable<Material, MaterialValues> materials = new();
    private static ConditionalWeakTable<Component, Transform> transforms = new();
    private static bool frameActive;
    private static Quaternion referenceRotation, referenceInverse;
    public static long SkippedWrites { get; private set; }

    internal static void Reset() { materials = new(); transforms = new(); frameActive = false; }
    internal static void ForgetMaterials(StarSimulator star)
    {
        if (star.bodyMaterial != null) materials.Remove(star.bodyMaterial);
        if (star.haloMaterial != null) materials.Remove(star.haloMaterial);
    }
    internal static void BeginFrame()
    {
        frameActive = GameMain.data != null;
        if (frameActive) { referenceRotation = GameMain.data.relativeRot; referenceInverse = Quaternion.Inverse(referenceRotation); }
    }
    internal static void EndFrame() => frameActive = false;
    internal static Quaternion Inverse(Quaternion rotation) => frameActive &&
        SameRotation(rotation, referenceRotation) ? referenceInverse : Quaternion.Inverse(rotation);
    private static unsafe bool SameRotation(Quaternion a, Quaternion b) =>
        ((long*)&a)[0] == ((long*)&b)[0] && ((long*)&a)[1] == ((long*)&b)[1];
    internal static Transform Transform(Component component) => transforms.GetValue(component, ReadTransform);
    private static Transform ReadTransform(Component component) => component.transform;

    internal static void Queue(Material material, int value)
    {
        var values = materials.GetOrCreateValue(material);
        if (values.Queue == value) { SkippedWrites++; return; }
        material.renderQueue = value; values.Queue = value;
    }
    internal static void Active(GameObject obj, bool active)
    {
        if (obj.activeSelf == active) { SkippedWrites++; return; }
        obj.SetActive(active);
    }
    internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var replacements = new Dictionary<MethodInfo, MethodInfo>
        {
            [AccessTools.PropertySetter(typeof(Material), nameof(Material.renderQueue))] = AccessTools.Method(typeof(VisualWrites), nameof(Queue)),
            [AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive))] = AccessTools.Method(typeof(VisualWrites), nameof(Active)),
            [AccessTools.Method(typeof(Quaternion), nameof(Quaternion.Inverse))] = AccessTools.Method(typeof(VisualWrites), nameof(Inverse)),
            [AccessTools.PropertyGetter(typeof(Component), nameof(Component.transform))] = AccessTools.Method(typeof(VisualWrites), nameof(Transform))
        };
        foreach (var instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                instruction.operand is MethodInfo method && replacements.TryGetValue(method, out var replacement))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
            }
            yield return instruction;
        }
    }
}

// Cutscenes and SetStarData write the same material parameters outside the optimized methods.
// Invalidate when the native world/simulator is rebuilt or the star's material setup changes.
[HarmonyPatch]
internal static class GalaxyDisplayCacheLifetime_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(StarSimulator), nameof(StarSimulator.SetStarData));
        yield return AccessTools.Method(typeof(StarSimulator), "OnDestroy");
        yield return AccessTools.Method(typeof(UniverseSimulator), nameof(UniverseSimulator.OnGameEnd));
    }
    [HarmonyPrefix]
    internal static void Prefix(object __instance)
    {
        VisualWrites.Reset();
        if (__instance is StarSimulator star) StarPresentation_Patch.Forget(star);
    }
}
