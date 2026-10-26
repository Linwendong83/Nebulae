#region

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Logger;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch(typeof(SiloComponent))]
internal class SiloComponent_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(SiloComponent.InternalUpdate))]
    private static IEnumerable<CodeInstruction> InternalUpdate_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        // Store projectile data after sphere.AddDysonRocket(dysonRocket, autoDysonNode) if IsUpdateNeeded == true
        var codeInstructions = instructions as CodeInstruction[] ?? instructions.ToArray();
        try
        {
            var matcher = new CodeMatcher(codeInstructions)
                .MatchForward(false,
                    new CodeMatch(OpCodes.Callvirt,
                        AccessTools.Method(typeof(DysonSphere), nameof(DysonSphere.AddDysonRocket))) //IL#310
                );
            var loadInstruction = matcher.InstructionAt(-1); //autoDysonNode
            matcher.Advance(2)
                .InsertAndAdvance(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(SiloComponent), nameof(SiloComponent.planetId))),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(SiloComponent), nameof(SiloComponent.localPos))),
                    loadInstruction,
                    HarmonyLib.Transpilers.EmitDelegate<Action<int, Vector3, DysonNode>>((planetId, localPos, autoDysonNode) =>
                    {
                        // If the dyson sphere has no subscribers anymore, skip this data
                        if (!Multiplayer.IsActive || !Multiplayer.Session.Launch.Snapshots.ContainsKey(planetId / 100 - 1))
                        {
                            return;
                        }

                        var data = new DysonLaunchData.Projectile
                        {
                            PlanetId = planetId,
                            LayerId = autoDysonNode.layerId,
                            TargetId = autoDysonNode.id,
                            LocalPos = localPos
                        };
                        Multiplayer.Session.Launch.ProjectileBag.Add(data);
                    })
                );
            return matcher.InstructionEnumeration();
        }
        catch (System.Exception error)
        {
            Log.Error("SiloComponent.InternalUpdate_Transpiler failed. Mod version not compatible with game version.", error);
            return codeInstructions;
        }
    }
}
