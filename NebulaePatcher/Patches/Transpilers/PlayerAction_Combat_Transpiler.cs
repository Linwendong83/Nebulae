#region

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Logger;

#endregion

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch(typeof(PlayerAction_Combat))]
internal class PlayerAction_Combat_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(PlayerAction_Combat.ShootTarget))]
    public static IEnumerable<CodeInstruction> ShootTarget_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            /*  Overwrite casterId in AddGroundEnemyHatred and AddSpaceEnemyHatred to playerId
            from:
                this.skillSystem.AddGroundEnemyHatred(dfgbaseComponent, ref ptr, ETargetType.Player, 1);
            to:
                this.skillSystem.AddGroundEnemyHatred(dfgbaseComponent, ref ptr, ETargetType.Player, NebulaeWorld.Combat.CombatManager.PlayerId);
            */

            var codeMatcher = new CodeMatcher(instructions)
                .End()
                .MatchBack(false,
                    new CodeMatch(OpCodes.Ldc_I4_1),
                    new CodeMatch(i => i.opcode == OpCodes.Callvirt && ((MethodInfo)i.operand).Name == "AddSpaceEnemyHatred"))
                .Set(OpCodes.Call, AccessTools.DeclaredPropertyGetter(typeof(NebulaeWorld.Combat.CombatManager),
                    nameof(NebulaeWorld.Combat.CombatManager.PlayerId)))
                .MatchBack(false,
                    new CodeMatch(OpCodes.Ldc_I4_1),
                    new CodeMatch(i => i.opcode == OpCodes.Callvirt && ((MethodInfo)i.operand).Name == "AddGroundEnemyHatred"))
                .Set(OpCodes.Call, AccessTools.DeclaredPropertyGetter(typeof(NebulaeWorld.Combat.CombatManager),
                    nameof(NebulaeWorld.Combat.CombatManager.PlayerId)));

            return codeMatcher.InstructionEnumeration();
        }
        catch (System.Exception e)
        {
            Log.Error("Transpiler PlayerAction_Combat.Shoot failed.");
            Log.Error(e);
            return instructions;
        }
    }

    [HarmonyTranspiler]
    [HarmonyPatch(nameof(PlayerAction_Combat.Bombing))]
    [HarmonyPatch(nameof(PlayerAction_Combat.Shoot_Gauss_Local))]
    [HarmonyPatch(nameof(PlayerAction_Combat.Shoot_Cannon_Local))]
    [HarmonyPatch(nameof(PlayerAction_Combat.Shoot_Plasma))]
    [HarmonyPatch(nameof(PlayerAction_Combat.Shoot_Missile))]
    [HarmonyPatch(nameof(PlayerAction_Combat.Shoot_Gauss_Space))]
    [HarmonyPatch(nameof(PlayerAction_Combat.Shoot_Cannon_Space))]
    [HarmonyPatch(nameof(PlayerAction_Combat.Shoot_Laser_Local))]
    [HarmonyPatch(nameof(PlayerAction_Combat.Shoot_Laser_Space))]
    [HarmonyPatch(nameof(PlayerAction_Combat.ShieldBurst))]
    public static IEnumerable<CodeInstruction> ReplacePlayerId_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            /*  Overwrite caster id to playerId
            from:
                ptr.caster.id = 1;
            to:
                ptr.caster.id = NebulaeWorld.Combat.CombatManager.PlayerId;
            */

            var codeMatcher = new CodeMatcher(instructions)
                .MatchForward(true,
                    new CodeMatch(i => i.opcode == OpCodes.Ldflda && ((FieldInfo)i.operand).Name == "caster"),
                    new CodeMatch(OpCodes.Ldc_I4_1),
                    new CodeMatch(i => i.opcode == OpCodes.Stfld && ((FieldInfo)i.operand).Name == "id"))
                .Repeat(matcher => matcher
                    .Advance(-1)
                    .Set(OpCodes.Call, AccessTools.DeclaredPropertyGetter(typeof(NebulaeWorld.Combat.CombatManager),
                        nameof(NebulaeWorld.Combat.CombatManager.PlayerId)))
                );

            return codeMatcher.InstructionEnumeration();
        }
        catch (System.Exception e)
        {
            Log.Error("Transpiler PlayerAction_Combat.Shoot failed.");
            Log.Error(e);
            return instructions;
        }
    }
}
