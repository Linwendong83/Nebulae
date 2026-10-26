using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Combat;
using UnityEngine;

namespace NebulaePatcher.Patches.Authority;

// Keep the native turret rules, but supply the selected pilot rather than the
// dedicated server's dead camera player. Never write shared SkillSystem inputs.
[HarmonyPatch(typeof(DFSTurretComponent))]
internal static class SpaceTurretPlayerInput_Patch
{
    [HarmonyPrefix, HarmonyPatch(nameof(DFSTurretComponent.LogicTick))]
    private static void Logic(ref DFSTurretComponent __instance, ref bool is_player_in_range)
    {
        if (!Multiplayer.IsActive) return;
        var sector = GameMain.spaceSector;
        var hive = sector.GetHiveByAstroId(sector.enemyPool[__instance.enemyId].originAstroId);
        is_player_in_range = false;
        if (hive == null) return;
        foreach (var pilot in Multiplayer.Session.Combat.Players)
        {
            if (!pilot.isAlive || pilot.starId != hive.starData.id) continue;
            sector.InverseTransformToAstro(hive.hiveAstroId, pilot.uPosition, out var local);
            if (local.sqrMagnitude >= 900000000.0) continue;
            is_player_in_range = true;
            break;
        }
    }

    private static bool Pilot(int id, out CombatManager.PlayerPosition pilot)
    {
        pilot = default;
        var combat = Multiplayer.Session?.Combat;
        if (combat == null || !combat.IndexByPlayerId.TryGetValue(id, out var index)) return false;
        pilot = combat.Players[index];
        return pilot.isAlive;
    }

    // Called directly by the Aim transpiler. Detouring this struct instance method's
    // Vector3 return corrupts the receiver on the game's Mono runtime (hidden sret ABI).
    internal static Vector3 Aim(ref DFSTurretComponent turret, EnemyDFHiveSystem hive, ref SkillTargetLocal target, ref VectorLF3 pos, ref Quaternion rot)
    {
        if (!Multiplayer.IsActive || target.type != ETargetType.Player) return turret.GetAimTargetDir(hive, ref target, ref pos, ref rot);
        if (!Pilot(target.id, out var pilot)) return default;
        hive.sector.InverseTransformToAstro(hive.hiveAstroId, pilot.uPosition, out var local);
        return Maths.QInvRotate(rot, (Vector3)local - ((Vector3)pos + rot.Up() * 30.4f));
    }

    [HarmonyTranspiler, HarmonyPatch(nameof(DFSTurretComponent.Shoot_Plasma))]
    internal static IEnumerable<CodeInstruction> Plasma(IEnumerable<CodeInstruction> instructions)
    {
        var position = AccessTools.Field(typeof(SkillSystem), nameof(SkillSystem.playerSkillTargetU));
        var velocity = AccessTools.Field(typeof(SkillSystem), nameof(SkillSystem.playerVelocityU));
        var replaced = 0;
        foreach (var code in instructions)
        {
            var method = code.LoadsField(position) ? nameof(Position) : code.LoadsField(velocity) ? nameof(Velocity) : null;
            if (method != null)
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code).MoveBlocksFrom(code);
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(SpaceTurretPlayerInput_Patch), method);
                replaced++;
            }
            yield return code;
        }
        if (replaced != 2) throw new InvalidOperationException("Native space turret plasma player inputs changed.");
    }

    private static VectorLF3 Position(SkillSystem skills, ref DFSTurretComponent turret) =>
        !Multiplayer.IsActive ? skills.playerSkillTargetU : Pilot(turret.target.id, out var pilot) ? pilot.skillTargetU : default;
    private static Vector3 Velocity(SkillSystem skills, ref DFSTurretComponent turret) =>
        !Multiplayer.IsActive ? skills.playerVelocityU : Pilot(turret.target.id, out var pilot) ? (Vector3)pilot.mecha.player.uVelocity : default;
}
