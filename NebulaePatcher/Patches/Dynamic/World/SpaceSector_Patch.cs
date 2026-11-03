#region

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic.World;

[HarmonyPatch(typeof(SpaceSector))]
internal class SpaceSector_Patch
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(SpaceSector.SetForNewGame))]
    public static IEnumerable<CodeInstruction> SetForNewGame_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var count = AccessTools.Field(typeof(StarData), nameof(StarData.initialHiveCount));
        if (code.Count(instruction => instruction.LoadsField(count)) != 1)
            throw new InvalidOperationException("SpaceSector initial hive generation changed.");
        // Keep astro slots, combat mode and all native subsystem initialization. Only the
        // generated hives are omitted: the client's authority baseline supplies them later.
        return code.Select(instruction => instruction.LoadsField(count)
            ? new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SpaceSector_Patch), nameof(InitialHiveCount)))
                .WithLabels(instruction.labels).WithBlocks(instruction.blocks)
            : instruction);
    }

    internal static int InitialHiveCount(StarData star) => Multiplayer.Session?.IsClient == true ? 0 : star.initialHiveCount;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SpaceSector.SetForNewGame))]
    public static void SetForNewGame_Prefix(SpaceSector __instance)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient)
        {
            // Ensure isCombatMode is properly set so SectorModel and PrefabDescByModelIndex are initialized on client
            __instance.isCombatMode = __instance.gameData?.gameDesc?.isCombatMode ?? true;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SpaceSector.KillEnemyFinal))]
    public static bool KillEnemyFinal_Prefix(int enemyId)
    {
        if (!Multiplayer.IsActive || enemyId <= 0)
        {
            return true;
        }
        if (Multiplayer.Session.IsServer)
        {
            // Host runs the death rule once; the replica lifecycle carries the removal.
            return true;
        }
        if (Multiplayer.Session.Enemies.IsIncomingRequest.Value)
        {
            return true;
        }

        // Keep the enemy targetable until the host confirms its death.
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SpaceSector.RemoveEnemyWithComponents))]
    public static void RemoveEnemyWithComponents_Prefix(SpaceSector __instance, int id)
    {
        // Fix IndexOutOfRangeException in SpaceSector.RemoveEnemyWithComponents IL_026A 
        // This is due to combatStats is not sync in client
        if (id != 0 && __instance.enemyPool[id].id != 0)
        {
            if (__instance.enemyPool[id].combatStatId != 0)
            {
                if (__instance.enemyPool[id].combatStatId >= __instance.skillSystem.combatStats.cursor)
                    __instance.enemyPool[id].combatStatId = 0;
            }
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SpaceSector.TryCreateNewHive))]
    public static bool TryCreateNewHive_Prefix()
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient) return Multiplayer.Session.Enemies.IsIncomingRequest;

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SpaceSector.GameTick))]
    public static void GameTick_Prefix(SpaceSector __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return;

        // Prevent purging valid space enemies with SetEmpty()
        if (__instance.model == null || SpaceSector.PrefabDescByModelIndex == null) return;
    }
}
