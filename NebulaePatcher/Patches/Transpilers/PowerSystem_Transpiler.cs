#region

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch(typeof(PowerSystem))]
internal class PowerSystem_Transpiler
{
    internal static bool ChargerSyncEnabled { get; private set; }

    [HarmonyTranspiler]
    [HarmonyPatch(nameof(PowerSystem.GameTick))]
    public static IEnumerable<CodeInstruction> PowerSystem_GameTick_Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator iLGenerator)
    {
        var original = instructions.ToArray();
        ChargerSyncEnabled = false;
        try
        {
            var result = PatchChargerInstructions(original, iLGenerator);
            ChargerSyncEnabled = true;
            return result;
        }
        catch (System.Exception e)
        {
            Log.Warn("PowerSystem.GameTick charger transpiler failed. Wireless charger synchronization is disabled.");
            Log.Warn(e);
            return original;
        }
    }

    // Work on independent instruction/metadata lists so a failed match cannot corrupt the fallback.
    internal static List<CodeInstruction> PatchChargerInstructions(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var code = instructions.Select(instruction => new CodeInstruction(instruction.opcode, instruction.operand)
        {
            labels = new List<Label>(instruction.labels),
            blocks = new List<ExceptionBlock>(instruction.blocks)
        }).ToList();
        var chargerField = AccessTools.Field(typeof(PowerNodeComponent), nameof(PowerNodeComponent.isCharger));
        var radiusField = AccessTools.Field(typeof(PowerNodeComponent), nameof(PowerNodeComponent.coverRadius));
        var requiredField = AccessTools.Field(typeof(PowerNodeComponent), nameof(PowerNodeComponent.requiredEnergy));
        var matcher = new CodeMatcher(code)
            .MatchForward(false,
                new CodeMatch(OpCodes.Ldfld, chargerField),
                new CodeMatch(i => i.opcode == OpCodes.Brfalse || i.opcode == OpCodes.Brfalse_S),
                new CodeMatch(i => i.IsLdloc()),
                new CodeMatch(OpCodes.Ldfld, radiusField));
        if (matcher.IsInvalid) throw new System.InvalidOperationException("Charger demand entry was not found.");
        var demandStart = matcher.Pos + 2;
        var nodeLoad = new CodeInstruction(code[demandStart].opcode, code[demandStart].operand);

        matcher.Advance(4).MatchForward(false,
            new CodeMatch(i => i.IsLdloc()),
            new CodeMatch(OpCodes.Ldfld, requiredField),
            new CodeMatch(OpCodes.Conv_I8),
            new CodeMatch(i => i.IsStloc()));
        if (matcher.IsInvalid) throw new System.InvalidOperationException("Charger demand accumulator was not found.");
        var demandEnd = matcher.Pos;

        matcher.End().MatchBack(false,
            new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(PowerNodeComponent), nameof(PowerNodeComponent.id))),
            new CodeMatch(i => i.IsLdloc()),
            new CodeMatch(i => i.opcode == OpCodes.Bne_Un || i.opcode == OpCodes.Bne_Un_S));
        if (matcher.IsInvalid) throw new System.InvalidOperationException("Charger animation loop was not found.");
        var nodeIdLoad = new CodeInstruction(matcher.InstructionAt(1).opcode, matcher.InstructionAt(1).operand);
        matcher.MatchForward(false,
            new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(AnimData), nameof(AnimData.state))),
            new CodeMatch(OpCodes.Ldc_I4_2),
            new CodeMatch(i => i.opcode == OpCodes.Bne_Un || i.opcode == OpCodes.Bne_Un_S));
        if (matcher.IsInvalid || matcher.InstructionAt(2).operand is not Label continueLabel)
            throw new System.InvalidOperationException("Charger energy loop exit was not found.");
        var energyStart = matcher.Pos + 3;
        if (demandEnd >= energyStart || !code.Any(i => i.labels.Contains(continueLabel)))
            throw new System.InvalidOperationException("Invalid charger control flow.");

        // All matches succeeded. Insert from the end so earlier indices remain stable.
        // Bypass the vanilla lock block to avoid the BepInEx 5.4.22/HarmonyX lock transpilation bug.
        var energyCall = new CodeInstruction(OpCodes.Ldarg_0);
        energyCall.labels.AddRange(code[energyStart].labels);
        code[energyStart].labels.Clear();
        code.InsertRange(energyStart, new[]
        {
            energyCall, nodeIdLoad,
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PowerSystem_Transpiler), nameof(AddMechaEnergy))),
            new CodeInstruction(OpCodes.Br, continueLabel)
        });

        // The accumulator has its own label. Never reuse the labeled load as an inserted instruction.
        var sumLabel = generator.DefineLabel();
        code[demandEnd].labels.Add(sumLabel);
        var demandCall = new CodeInstruction(OpCodes.Ldarg_0);
        demandCall.labels.AddRange(code[demandStart].labels);
        code[demandStart].labels.Clear();
        code.InsertRange(demandStart, new[]
        {
            demandCall, nodeLoad,
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PowerSystem_Transpiler), nameof(SetChargerRequiredPower))),
            new CodeInstruction(OpCodes.Brtrue, sumLabel)
        });

        // Insertion can push an existing short branch beyond its signed-byte offset.
        var longBranches = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null))
            .Where(opcode => opcode.OperandType == OperandType.InlineBrTarget).ToDictionary(opcode => opcode.Name);
        foreach (var instruction in code)
        {
            if (instruction.opcode.OperandType == OperandType.ShortInlineBrTarget)
                instruction.opcode = longBranches[instruction.opcode.Name.Substring(0, instruction.opcode.Name.Length - 2)];
        }
        return code;
    }

    private static void AddMechaEnergy(PowerSystem powerSystem, int nodeId)
    {
        var player = powerSystem.factory.gameData.mainPlayer;
        if (Multiplayer.IsActive && (player.planetId != powerSystem.factory.planetId || !player.isAlive ||
            !Multiplayer.Session.PowerTowers.IsLocalCharging(powerSystem.factory.planetId, nodeId))) return;

        // Personal mecha energy still runs locally, like movement and reactor consumption.
        // The world replica does not carry player energy. Refusing this write consumes grid
        // power without charging the player; remote demand is already excluded above.

        ref var powerNode = ref powerSystem.nodePool[nodeId];
        // Multiplayer demand includes every charging player; each mecha receives only its own share.
        var chargingEnergy = Multiplayer.IsActive && powerNode.coverRadius <= 20f
            ? powerNode.workEnergyPerTick - powerNode.idleEnergyPerTick
            : powerNode.requiredEnergy - powerNode.idleEnergyPerTick;
        var energyCharged = (int)(chargingEnergy * powerSystem.networkServes[powerNode.networkId]);
        var mecha = player.mecha;
        lock (mecha)
        {
            mecha.coreEnergy += energyCharged;
            mecha.MarkEnergyChange(2, energyCharged);
            mecha.AddChargerDevice(powerNode.entityId);
            if (mecha.coreEnergy > mecha.coreEnergyCap) mecha.coreEnergy = mecha.coreEnergyCap;
        }
    }

    private static bool SetChargerRequiredPower(PowerSystem powerSystem, ref PowerNodeComponent node)
    {
        if (!Multiplayer.IsActive || node.coverRadius > 20f) return false;
        var count = Multiplayer.Session.PowerTowers.GetChargerCount(powerSystem.factory.planetId, node.id);
        node.requiredEnergy = node.idleEnergyPerTick + count * (node.workEnergyPerTick - node.idleEnergyPerTick);
        return true;
    }

    [HarmonyTranspiler]
    [HarmonyPatch(nameof(PowerSystem.RequestDysonSpherePower))]
    public static IEnumerable<CodeInstruction> PowerSystem_RequestDysonSpherePower_Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        //Prevent dysonSphere.energyReqCurrentTick from changing on the client side
        //Change: if (this.dysonSphere != null) dysonSphere.energyReqCurrentTick += num;
        //To:     if (this.dysonSphere != null && (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)) ...
        var codeInstructions = instructions as CodeInstruction[] ?? instructions.ToArray();
        try
        {
            var codeMatcher = new CodeMatcher(codeInstructions)
                .End()
                .MatchBack(true,
                    new CodeMatch(i => i.IsLdloc()),
                    new CodeMatch(OpCodes.Brfalse) //IL #66
                );
            var label = codeMatcher.Instruction.operand;
            codeMatcher.Advance(1)
                .InsertAndAdvance(HarmonyLib.Transpilers.EmitDelegate(() =>
                    !Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost))
                .InsertAndAdvance(new CodeInstruction(OpCodes.Brfalse_S, label));
            return codeMatcher.InstructionEnumeration();
        }
        catch (System.Exception e)
        {
            Log.Error("PowerSystem.RequestDysonSpherePower_Transpiler failed. Dyson power generation on client will be incorrect.");
            Log.Warn(e);
            return codeInstructions;
        }
    }
}
