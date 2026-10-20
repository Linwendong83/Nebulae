using HarmonyLib;
using NebulaeWorld;
using UnityEngine;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(SkillSystem))]
internal static class PlayerSkillTarget_Patch
{
    private static bool Handle(in SkillTarget obj, out Mecha mecha)
    {
        mecha = null;
        if (!Multiplayer.IsActive || obj.type != ETargetType.Player) return false;
        if (!Multiplayer.Session.IsDedicated && obj.id == Multiplayer.Session.LocalPlayer.Id) mecha = GameMain.mainPlayer.mecha;
        else if (obj.id > 0 && obj.id <= ushort.MaxValue) mecha = Multiplayer.Session.CombatAuthority.MechaFor((ushort)obj.id);
        if (mecha == null && Multiplayer.Session.Combat.IndexByPlayerId.TryGetValue(obj.id, out var index) && Multiplayer.Session.Combat.Players[index].isAlive)
            mecha = Multiplayer.Session.Combat.Players[index].mecha;
        return true;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(SkillSystem.GetObjectUPosition))]
    private static bool Position(ref SkillTarget obj, ref VectorLF3 upos, ref bool __result)
    {
        if (!Handle(obj, out var mecha)) return true;
        upos = mecha?.skillTargetUCenter ?? default; __result = mecha?.player.isAlive == true; return false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(SkillSystem.GetObjectUPose))]
    private static bool Pose(ref SkillTarget obj, ref VectorLF3 upos, ref Quaternion urot, ref bool __result)
    {
        if (!Handle(obj, out var mecha)) return true;
        upos = mecha?.skillTargetUCenter ?? default; urot = mecha?.player.uRotation ?? Quaternion.identity;
        __result = mecha?.player.isAlive == true; return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.GetObjectUPositionAndVelocity), [typeof(SkillTarget), typeof(VectorLF3), typeof(Vector3)], [ArgumentType.Ref, ArgumentType.Out, ArgumentType.Out])]
    private static bool PositionVelocity(ref SkillTarget obj, ref VectorLF3 upos, ref Vector3 uvel, ref bool __result)
    {
        if (!Handle(obj, out var mecha)) return true;
        upos = mecha?.skillTargetUCenter ?? default; uvel = mecha == null ? default : (Vector3)mecha.player.uVelocity;
        __result = mecha?.player.isAlive == true; return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.GetObjectUPositionAndVelocity), [typeof(SkillTarget), typeof(VectorLF3), typeof(Vector3), typeof(Vector3)], [ArgumentType.Ref, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out])]
    private static bool PositionVelocities(ref SkillTarget obj, ref VectorLF3 upos, ref Vector3 uvel_astro, ref Vector3 uvel_obj, ref bool __result)
    {
        if (!Handle(obj, out var mecha)) return true;
        upos = mecha?.skillTargetUCenter ?? default; uvel_astro = default; uvel_obj = mecha == null ? default : (Vector3)mecha.player.uVelocity;
        __result = mecha?.player.isAlive == true; return false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(SkillSystem.GetObjectUVelocity))]
    private static bool Velocity(ref SkillTarget obj, ref Vector3 uvel)
    {
        if (!Handle(obj, out var mecha)) return true;
        uvel = mecha == null ? default : (Vector3)mecha.player.uVelocity; return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.GetObjectLVelocity), [typeof(SkillTarget), typeof(Vector3)], [ArgumentType.Ref, ArgumentType.Out])]
    private static bool LocalVelocity(ref SkillTarget obj, ref Vector3 lvel)
    {
        if (!Handle(obj, out var mecha)) return true;
        lvel = mecha?.player.controller.velocity ?? default; return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.GetObjectLVelocity), [typeof(SkillTargetLocal), typeof(PlanetFactory), typeof(Vector3)], [ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Out])]
    private static bool LocalVelocityInFactory(ref SkillTargetLocal obj, ref Vector3 lvel)
    {
        var target = new SkillTarget { type = obj.type, id = obj.id };
        return LocalVelocity(ref target, ref lvel);
    }
}
