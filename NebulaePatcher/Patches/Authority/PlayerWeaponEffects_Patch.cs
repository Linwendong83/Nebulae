using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld.Combat;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

/// <summary>The host captures effects at the shot's planet, even when its own camera is elsewhere.</summary>
[HarmonyPatch]
internal static class PlayerWeaponEffects_Patch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var types = new[] { typeof(LocalGeneralProjectile), typeof(LocalLaserOneShot), typeof(SpaceLaserOneShot),
            typeof(GeneralProjectile), typeof(GeneralMissile), typeof(LocalCannonade), typeof(GeneralCannonade),
            typeof(Bomb_Explosive), typeof(Bomb_Liquid), typeof(Bomb_EMCapsule), typeof(GeneralShieldBurst) };
        return types.SelectMany(AccessTools.GetDeclaredMethods).Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(SkillSystem)))
            .Concat(new[] { AccessTools.Method(typeof(PlayerAction_Combat), nameof(PlayerAction_Combat.ShieldBurst)) });
    }
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var field = AccessTools.Field(typeof(SkillSystem), nameof(SkillSystem.localAstroId));
        foreach (var instruction in instructions)
        {
            if (instruction.LoadsField(field))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(PlayerWeaponManager), nameof(PlayerWeaponManager.EffectAstro)); }
            else if (instruction.LoadsField(AccessTools.Field(typeof(SkillSystem), nameof(SkillSystem.localPlanetOrStarAstroId))))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(PlayerWeaponEffects_Patch), nameof(CasterAstro)); }
            yield return instruction;
        }
    }
    private static int CasterAstro(SkillSystem skills)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsServer) return skills.localPlanetOrStarAstroId;
        var player = Multiplayer.Session.CombatAuthority.MechaFor((ushort)CombatManager.PlayerId)?.player;
        return player == null ? skills.localPlanetOrStarAstroId : player.planetId > 0 ? player.planetId : CombatAuthorityManager.CasterStar()?.astroId ?? 0;
    }
}
