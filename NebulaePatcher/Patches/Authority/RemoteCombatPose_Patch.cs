using HarmonyLib;
using NebulaeWorld.Combat;
using UnityEngine;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(Mecha))]
internal static class RemoteCombatPose_Patch
{
    [HarmonyPrefix, HarmonyPatch(nameof(Mecha.UpdateSkillColliders))]
    public static bool Colliders(Mecha __instance)
    {
        if (!RemoteCombatPose.Owns(__instance)) return true;
        RemoteCombatPose.UpdateColliders(__instance);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(Mecha.skillTargetLCenter), MethodType.Getter)]
    public static bool TargetLocal(Mecha __instance, ref Vector3 __result) => Local(__instance, __instance.player?.animator?.skillTargetBone, ref __result);
    [HarmonyPrefix, HarmonyPatch(nameof(Mecha.skillTargetUCenter), MethodType.Getter)]
    public static bool TargetUniversal(Mecha __instance, ref VectorLF3 __result) => Universal(__instance, __instance.player?.animator?.skillTargetBone, ref __result);
    [HarmonyPrefix, HarmonyPatch(nameof(Mecha.skillCastLeftL), MethodType.Getter)]
    public static bool LeftLocal(Mecha __instance, ref Vector3 __result) => Local(__instance, __instance.player?.animator?.skillLeftTurretBone, ref __result);
    [HarmonyPrefix, HarmonyPatch(nameof(Mecha.skillCastLeftU), MethodType.Getter)]
    public static bool LeftUniversal(Mecha __instance, ref VectorLF3 __result) => Universal(__instance, __instance.player?.animator?.skillLeftTurretBone, ref __result);
    [HarmonyPrefix, HarmonyPatch(nameof(Mecha.skillCastRightL), MethodType.Getter)]
    public static bool RightLocal(Mecha __instance, ref Vector3 __result) => Local(__instance, __instance.player?.animator?.skillRightTurretBone, ref __result);
    [HarmonyPrefix, HarmonyPatch(nameof(Mecha.skillCastRightU), MethodType.Getter)]
    public static bool RightUniversal(Mecha __instance, ref VectorLF3 __result) => Universal(__instance, __instance.player?.animator?.skillRightTurretBone, ref __result);
    [HarmonyPrefix, HarmonyPatch(nameof(Mecha.skillShieldBurstLCenter), MethodType.Getter)]
    public static bool BurstLocal(Mecha __instance, ref VectorLF3 __result)
    {
        if (!RemoteCombatPose.Owns(__instance)) return true;
        __result = RemoteCombatPose.LocalBone(__instance, __instance.player.animator.skillShieldBurstBone);
        return false;
    }
    [HarmonyPrefix, HarmonyPatch(nameof(Mecha.skillShieldBurstUCenter), MethodType.Getter)]
    public static bool BurstUniversal(Mecha __instance, ref VectorLF3 __result) => Universal(__instance, __instance.player?.animator?.skillShieldBurstBone, ref __result);

    [HarmonyPrefix, HarmonyPatch(nameof(Mecha.skillBombingLCenter), MethodType.Getter)]
    public static bool BombLocal(Mecha __instance, ref VectorLF3 __result)
    {
        if (!RemoteCombatPose.Owns(__instance)) return true;
        __result = RemoteCombatPose.LocalBone(__instance, __instance.player.animator.skillBombingBone);
        return false;
    }
    [HarmonyPrefix, HarmonyPatch(nameof(Mecha.skillBombingUCenter), MethodType.Getter)]
    public static bool BombUniversal(Mecha __instance, ref VectorLF3 __result) => Universal(__instance, __instance.player?.animator?.skillBombingBone, ref __result);

    private static bool Local(Mecha mecha, Transform bone, ref Vector3 result)
    {
        if (!RemoteCombatPose.Owns(mecha) || bone == null) return true;
        result = RemoteCombatPose.LocalBone(mecha, bone);
        return false;
    }
    private static bool Universal(Mecha mecha, Transform bone, ref VectorLF3 result)
    {
        if (!RemoteCombatPose.Owns(mecha) || bone == null) return true;
        result = RemoteCombatPose.UniversalBone(mecha, bone);
        return false;
    }
}
