using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

/// <summary>Keep vanilla laboratory consumption; global research commits belong to the host.</summary>
[HarmonyPatch(typeof(FactorySystem), nameof(FactorySystem.GameTickLabResearchMode))]
internal static class LaboratoryResearch_Patch
{
    private static bool Client => Multiplayer.IsActive && Multiplayer.Session.IsClient;
    private static TechState Read(Dictionary<int, TechState> states, int tech)
    {
        var state = states[tech];
        return Client ? Multiplayer.Session.History.LaboratoryResearch.Read(Multiplayer.Session.Authority.Epoch, tech, state) : state;
    }
    private static void Write(Dictionary<int, TechState> states, int tech, TechState state)
    {
        if (Client) Multiplayer.Session.History.LaboratoryResearch.Write(tech, state);
        else states[tech] = state;
    }
    private static void RecordGlobalHashes(GameStatData statistics, int hashes)
    {
        if (!Client) statistics.techHashedThisFrame = hashes;
    }
    private static void RecordUniversePoints(GameHistoryData history, long points)
    {
        if (!Client) history.universeMatrixPointUploaded = points;
    }
    private static void EnterWrite(ref SimpleReadWriteLock gate) { if (!Client) gate.EnterWrite(); }
    private static void ExitWrite(ref SimpleReadWriteLock gate) { if (!Client) gate.ExitWrite(); }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var original = instructions.ToArray(); var output = original.Select(instruction => new CodeInstruction(instruction)).ToArray();
        var get = AccessTools.PropertyGetter(typeof(Dictionary<int, TechState>), "Item");
        var set = AccessTools.PropertySetter(typeof(Dictionary<int, TechState>), "Item");
        var replacements = new Dictionary<MethodInfo, MethodInfo>
        {
            [get] = AccessTools.Method(typeof(LaboratoryResearch_Patch), nameof(Read)),
            [set] = AccessTools.Method(typeof(LaboratoryResearch_Patch), nameof(Write)),
            [AccessTools.Method(typeof(SimpleReadWriteLock), nameof(SimpleReadWriteLock.EnterWrite))] = AccessTools.Method(typeof(LaboratoryResearch_Patch), nameof(EnterWrite)),
            [AccessTools.Method(typeof(SimpleReadWriteLock), nameof(SimpleReadWriteLock.ExitWrite))] = AccessTools.Method(typeof(LaboratoryResearch_Patch), nameof(ExitWrite))
        };
        var writes = 0; var reads = 0; var counters = 0;
        foreach (var instruction in output)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo method && replacements.TryGetValue(method, out var replacement))
            {
                if (method == get) reads++;
                if (method == set) writes++;
                instruction.opcode = OpCodes.Call; instruction.operand = replacement;
            }
            else if (instruction.opcode == OpCodes.Stfld && instruction.operand is FieldInfo field &&
                (field == AccessTools.Field(typeof(GameStatData), nameof(GameStatData.techHashedThisFrame)) ||
                 field == AccessTools.Field(typeof(GameHistoryData), nameof(GameHistoryData.universeMatrixPointUploaded))))
            {
                counters++;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(LaboratoryResearch_Patch),
                    field.DeclaringType == typeof(GameStatData) ? nameof(RecordGlobalHashes) : nameof(RecordUniversePoints));
            }
        }
        if (reads != 1 || writes != 3 || counters != 2) throw new InvalidOperationException("Native laboratory research ownership hooks do not match this game version.");
        return output;
    }
}
