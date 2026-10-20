using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;
using UnityEngine;

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch(typeof(StationComponent))]
internal class StationComponent_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(StationComponent.InternalTickRemote))]
    public static IEnumerable<CodeInstruction> InternalTickRemote_Transpiler(IEnumerable<CodeInstruction> instructions)
        => StorageDeltas(instructions);

    [HarmonyTranspiler]
    [HarmonyPatch("DispatchSupplyShip")]
    public static IEnumerable<CodeInstruction> DispatchSupplyShip_Transpiler(IEnumerable<CodeInstruction> instructions)
        => StorageDeltas(instructions);

    private static IEnumerable<CodeInstruction> StorageDeltas(IEnumerable<CodeInstruction> instructions)
    {
        var matcher = new CodeMatcher(instructions);
        matcher.MatchForward(true,
            new CodeMatch(instruction => instruction.opcode == OpCodes.Ldflda && instruction.operand is FieldInfo field &&
                field.DeclaringType == typeof(StationStore) && (field.Name == nameof(StationStore.count) || field.Name == nameof(StationStore.inc))),
            new CodeMatch(OpCodes.Dup), new CodeMatch(OpCodes.Ldind_I4),
            new CodeMatch(instruction => IsLoad(instruction)), new CodeMatch(OpCodes.Sub), new CodeMatch(OpCodes.Stind_I4))
            .Repeat(local =>
            {
                var arguments = StorageUpdateArguments(local.Instructions(), local.Pos);
                if (arguments == null) throw new InvalidOperationException("Cannot capture native station inventory delta.");
                var increment = ((FieldInfo)local.InstructionAt(-5).operand).Name == nameof(StationStore.inc);
                var amount = local.InstructionAt(-2);
                local.Advance(1);
                foreach (var argument in arguments) local.InsertAndAdvance(new CodeInstruction(argument));
                local.InsertAndAdvance(new CodeInstruction(amount), new CodeInstruction(OpCodes.Neg),
                    new CodeInstruction(increment ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
                local.Insert(HarmonyLib.Transpilers.EmitDelegate<StorageDelta>((storage, index, delta, inc) =>
                {
                    if (Multiplayer.IsActive && Multiplayer.Session.IsServer)
                        Multiplayer.Session.Ships.RecordStorageDelta(storage, index, delta, inc);
                }));
            });
        return matcher.InstructionEnumeration();
    }

    private static bool IsLoad(CodeInstruction instruction)
    {
        var name = instruction.opcode.Name;
        return name.StartsWith("ldloc") && !name.StartsWith("ldloca") || name.StartsWith("ldarg") && !name.StartsWith("ldarga") || name.StartsWith("ldc.i4");
    }

    // Recover the actual array expression and slot, including array aliases introduced by the compiler.
    // A StationComponent and a StationStore[] are different receivers; neither can substitute for the other.
    internal static List<CodeInstruction> StorageUpdateArguments(IReadOnlyList<CodeInstruction> code, int stindIndex)
    {
        var address = stindIndex - 6;
        if (address < 2 || address >= code.Count || code[address].opcode != OpCodes.Ldelema ||
            !Equals(code[address].operand, typeof(StationStore))) return null;
        var indexStart = address - 1;
        if (code[indexStart].opcode == OpCodes.Ldfld)
        {
            if (code[indexStart].operand is not FieldInfo indexField || indexField.FieldType != typeof(int)) return null;
            indexStart--;
        }
        if (indexStart < 1 || !IsLoad(code[indexStart])) return null;
        var arrayStart = indexStart - 1;
        if (code[arrayStart].opcode == OpCodes.Ldfld)
        {
            if (code[arrayStart].operand is not FieldInfo arrayField || arrayField.FieldType != typeof(StationStore[])) return null;
            arrayStart--;
        }
        if (arrayStart < 0 || !IsLoad(code[arrayStart])) return null;
        var arguments = new List<CodeInstruction>();
        for (var i = arrayStart; i < address; i++) arguments.Add(code[i]);
        return arguments;
    }
    [HarmonyReversePatch(HarmonyReversePatchType.Original)]
    [HarmonyPatch(nameof(StationComponent.InternalTickRemote))]
    [SuppressMessage("Style", "IDE0060:Remove unused parameter")]
    public static void ILSUpdateShipPos(StationComponent stationComponent, PlanetFactory factory, int timeGene,
        float shipSailSpeed, float shipWarpSpeed, int shipCarries, StationComponent[] gStationPool, AstroData[] astroPoses,
        ref VectorLF3 relativePos, ref Quaternion relativeRot, bool starmap, int[] consumeRegister)
    {
        _ = Transpiler(null, null);
        return;

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var matcher = new CodeMatcher(instructions, il);
            int indexStart, indexEnd;

            // The native prelude refills station warpers from inventory. Only the host may do that.
            // Jump over the entire block (including its lock), preserving the flight setup after it.
            matcher.MatchForward(false,
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(StationComponent), nameof(StationComponent.warperCount))),
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(StationComponent), nameof(StationComponent.warperMaxCount))),
                new CodeMatch(instruction => instruction.opcode == OpCodes.Bge || instruction.opcode == OpCodes.Bge_S));
            if (matcher.IsInvalid) throw new System.InvalidOperationException("Cannot isolate native ship flight from warper refill.");
            for (var i = 0; i < 4; i++) matcher.SetAndAdvance(OpCodes.Nop, null);
            matcher.SetOpcodeAndAdvance(OpCodes.Br);
            matcher.Start();

            // Part1: Add null check for gStationPool[shipData.otherGId] at the beginning of the major while loop (c# 62)
            //        If it is null, skip this shipData until all data is received from server
            // 
            // 	while (j < this.workShipCount)
            //  {				
            //	   ref ShipData ptr2 = ref this.workShipDatas[j];
            //   >>  insert if (gStationPool[shipData.otherGId] == null) { j++; continue; }
            matcher
                .MatchForward(true,
                    new CodeMatch(OpCodes.Br),
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(StationComponent), "workShipDatas")),
                    new CodeMatch(OpCodes.Ldloc_S),
                    new CodeMatch(OpCodes.Ldelema, typeof(ShipData)),
                    new CodeMatch(OpCodes.Stloc_S));

            var shipDataRef = matcher.Instruction.operand;
            var loopIndex = matcher.InstructionAt(-2).operand;
            var jmpNextLoopIter = matcher.InstructionAt(-5).operand;
            matcher.CreateLabelAt(matcher.Pos + 1, out var jmpNormalFlow);
            matcher
                .Advance(1)
                .InsertAndAdvance(
                    new CodeInstruction(OpCodes.Ldarg_S, 6), // gStationPool
                    new CodeInstruction(OpCodes.Ldloc_S, shipDataRef), // shipData
                    CodeInstruction.Call(typeof(StationComponent_Transpiler), nameof(CanAdvanceShip)),
                    new CodeInstruction(OpCodes.Brtrue, jmpNormalFlow),
                    new CodeInstruction(OpCodes.Ldloc_S, loopIndex),
                    new CodeInstruction(OpCodes.Ldc_I4_1),
                    new CodeInstruction(OpCodes.Add),
                    new CodeInstruction(OpCodes.Stloc_S, loopIndex),
                    new CodeInstruction(OpCodes.Br, jmpNextLoopIter)
                );

            // Part2: Remove c# 97-121 (adding item from landing ship to station and modify remote order and shifitng those arrays AND j-- (as we end up in an endless loop if not))
            // start: this.AddItem(ptr2.itemId, ptr2.itemCount, ptr2.inc);
            // end:   j--;
            indexStart = matcher
                .MatchForward(false,
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(OpCodes.Ldloc_S),
                    new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(ShipData), "itemId")),
                    new CodeMatch(OpCodes.Ldloc_S),
                    new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(ShipData), "itemCount")),
                    new CodeMatch(OpCodes.Ldloc_S),
                    new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(ShipData), "inc")),
                    new CodeMatch(i => i.opcode == OpCodes.Call && ((MethodInfo)i.operand).Name == "AddItem"))
                .Pos;

            indexEnd = matcher
                .MatchForward(true,
                    new CodeMatch(OpCodes.Sub),
                    new CodeMatch(i => i.opcode == OpCodes.Call && ((MethodInfo)i.operand).Name == "Clear"),
                    new CodeMatch(OpCodes.Ldloc_S), // j--
                    new CodeMatch(OpCodes.Ldc_I4_1),
                    new CodeMatch(OpCodes.Sub),
                    new CodeMatch(OpCodes.Stloc_S))
                .Advance(1)
                .Pos;
            for (matcher.Start().Advance(indexStart); matcher.Pos < indexEnd;)
            {
                matcher.SetAndAdvance(OpCodes.Nop, null);
            }

            // Part4: Switch itemCount in ShipData when ship arrive destination to display correct color
            //        Currently the render only test if itemCount > 0 so we can give it a dummy positive value
            //
            //  c# 668-880
            //  if (ptr2.direction > 0)
            //  {
            //		ptr2.t -= 0.0334f;
            //		if (ptr2.t < 0f)
            //		{
            //          >> Change the content to following and skip the rest of the calculation
            //          ptr2.t = 0f;
            //			ptr2.direction = -1;
            //			ptr2.itemCount = ptr2.itemCount > 0 ? 0 : 1;
            //		} >> labelEnd
            //
            matcher
                .MatchForward(true,
                    new CodeMatch(OpCodes.Ldc_R4, 0.0334f),
                    new CodeMatch(OpCodes.Sub),
                    new CodeMatch(OpCodes.Stind_R4),
                    new CodeMatch(OpCodes.Ldloc_S, shipDataRef),
                    new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(ShipData), "t")),
                    new CodeMatch(OpCodes.Ldc_R4, 0.0f),
                    new CodeMatch(OpCodes.Bge_Un));
            var labelEnd = matcher.Operand;

            matcher
                .Advance(1)
                .Insert(
                    // ptr2.t = 0.0f;
                    new CodeInstruction(OpCodes.Ldloc_S, shipDataRef),
                    new CodeInstruction(OpCodes.Ldc_R4, 0.0f),
                    new CodeInstruction(OpCodes.Stfld, AccessTools.Field(typeof(ShipData), "t")),

                    // ptr2.direction = -1;
                    new CodeInstruction(OpCodes.Ldloc_S, shipDataRef),
                    new CodeInstruction(OpCodes.Ldc_I4_M1),
                    new CodeInstruction(OpCodes.Stfld, AccessTools.Field(typeof(ShipData), "direction")),

                    // ptr2.itemCount = ptr2.itemCount > 0 ? 0 : 1;
                    new CodeInstruction(OpCodes.Ldloc_S, shipDataRef),
                    new CodeInstruction(OpCodes.Ldloc_S, shipDataRef),
                    new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ShipData), "itemCount")),
                    new CodeInstruction(OpCodes.Ldc_I4_0), //CreateLabel labelTo0
                    new CodeInstruction(OpCodes.Stfld, AccessTools.Field(typeof(ShipData), "itemCount")),
                    new CodeInstruction(OpCodes.Br_S, labelEnd)
                )
                .Advance(9) //OpCodes.Ldc_I4_0
                .CreateLabel(out var labelTo0)
                .Insert(
                    new CodeInstruction(OpCodes.Ldc_I4_0),
                    new CodeInstruction(OpCodes.Bgt_S, labelTo0),
                    new CodeInstruction(OpCodes.Ldc_I4_1),
                    new CodeInstruction(OpCodes.Stfld, AccessTools.Field(typeof(ShipData), "itemCount")),
                    new CodeInstruction(OpCodes.Br_S, labelEnd)
                );

            return matcher.InstructionEnumeration();
        }
    }

    private delegate void StorageDelta(StationStore[] storage, int index, int delta, bool inc);

    // Native flight knows how to return when a destination was deleted (otherGId == 0).
    internal static bool CanAdvanceShip(StationComponent[] pool, ref ShipData ship)
    {
        if (ship.otherGId <= 0) return true;
        if (ship.otherGId >= pool.Length) return false;
        var destination = pool[ship.otherGId];
        // A destination the client has never loaded is a fake station whose dock pose is still the
        // zero placeholder, and native flight aims at that pose (the vanilla approach adds
        // 25 * shipDockPos.normalized to it). Advancing now would fly the ship into the planet's
        // centre until the gStationPoolSync carrying the real pose arrives, so wait instead — the
        // same treatment a destination that is still null already gets.
        return destination != null && destination.shipDockPos != Vector3.zero;
    }
}
