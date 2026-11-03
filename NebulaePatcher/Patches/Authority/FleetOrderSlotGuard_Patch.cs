using System;
using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

/// <summary>Clears fleet orders without trusting a replicated fleet key to still index the local pool.</summary>
/// <remarks>
/// <para>
/// A player's combat module arrives from the host carrying the host's <c>fleetId</c> and
/// <c>fleetAstroId</c> foreign keys, but the fleet component they name is replicated separately. Until
/// that replica is applied - and always when the host pool id is larger than the local capacity, which
/// starts at 8 - the key names a slot this peer cannot read. Vanilla indexes the pool directly, and
/// every respawn runs <c>CancelAllFleetsOrder</c> before the death action is reset, so one stale key
/// aborts <c>PlayerAction_Death.RespawnFinally</c> and leaves the player's death state half-applied
/// (<c>respawning</c> stays set, so the next death can never respawn again).
/// </para>
/// <para>
/// The fleet panel already refuses to read such a slot (<see cref="FleetEntryReadiness_Patch"/>);
/// these prefixes give the native order and death paths the same readiness rule. A live slot is still
/// cancelled exactly as vanilla would, an unreachable pool is left untouched, and a key that names
/// nothing in a reachable pool is dropped so the next replicated craft state can rebind it. The native
/// body still runs unchanged whenever every slot is live.
/// </para>
/// </remarks>
[HarmonyPatch]
internal static class FleetOrderSlotGuard_Patch
{
    private enum SlotState
    {
        Empty,
        Live,
        Stale,
        Unreachable
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.CancelAllFleetsOrder))]
    private static bool CancelAllFleetsOrder_Prefix(CombatModuleComponent __instance)
    {
        if (!Multiplayer.IsActive || __instance?.moduleFleets == null) return true;
        if (NativeIsSafe(__instance)) return true;
        var repaired = 0;
        var skipped = 0;
        Cancel(__instance, -1, ref repaired, ref skipped);
        Report(__instance, "all", repaired, skipped);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.CancelFleetOrder))]
    private static bool CancelFleetOrder_Prefix(CombatModuleComponent __instance, int fleetIndex)
    {
        if (!Multiplayer.IsActive || __instance?.moduleFleets == null) return true;
        // A stale UI index must not become an IndexOutOfRangeException in the native body.
        if (fleetIndex < 0 || fleetIndex >= __instance.fleetCount || fleetIndex >= __instance.moduleFleets.Length)
        {
            Diagnostics.Warn("fleet:slot:index", () => $"[fleet] ignoring order index {fleetIndex} outside count={__instance.fleetCount} slots={__instance.moduleFleets.Length}");
            return false;
        }
        if (NativeIsSafe(__instance)) return true;
        var repaired = 0;
        var skipped = 0;
        Cancel(__instance, fleetIndex, ref repaired, ref skipped);
        Report(__instance, "one", repaired, skipped);
        return false;
    }

    /// <summary>The native loop is only safe while every named slot resolves to a live fleet.</summary>
    private static bool NativeIsSafe(CombatModuleComponent module)
    {
        if (module.fleetCount < 0 || module.fleetCount > module.moduleFleets.Length) return false;
        // Native reads the module's own sector, not the current world; a stale or missing one would
        // throw before the pool is even reached, so only the fallback in Classify may repair that.
        var sector = module.sector;
        if (sector == null) return false;
        if (module.isSpace ? sector.combatSpaceSystem?.fleets?.buffer == null : sector.galaxy?.astrosFactory == null)
            return false;
        for (var i = 0; i < module.fleetCount; i++)
        {
            ref var slot = ref module.moduleFleets[i];
            if (slot.fleetId <= 0) continue;
            if (Classify(module, ref slot, out _) != SlotState.Live) return false;
        }
        return true;
    }

    private static void Cancel(CombatModuleComponent module, int only, ref int repaired, ref int skipped)
    {
        var count = Math.Min(Math.Max(module.fleetCount, 0), module.moduleFleets.Length);
        for (var i = 0; i < count; i++)
        {
            if (only >= 0 && i != only) continue;
            ref var slot = ref module.moduleFleets[i];
            if (slot.fleetId <= 0) continue;
            switch (Classify(module, ref slot, out var fleets))
            {
                case SlotState.Live:
                    fleets.buffer[slot.fleetId].CancelOrder();
                    break;
                case SlotState.Stale:
                    // The next replicated craft state rebuilds this key (NativeCraftSimulation.LinkOwnerModule).
                    slot.OnFleetComponentRemoved();
                    repaired++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }
    }

    private static SlotState Classify(CombatModuleComponent module, ref ModuleFleet slot,
        out DataPool<FleetComponent> fleets)
    {
        fleets = null;
        if (slot.fleetId <= 0) return SlotState.Empty;
        var sector = module.sector;
        if (module.isSpace)
        {
            fleets = sector?.combatSpaceSystem?.fleets ?? GameMain.data?.spaceSector?.combatSpaceSystem?.fleets;
        }
        else
        {
            var galaxy = sector?.galaxy ?? GameMain.galaxy;
            var factories = galaxy?.astrosFactory;
            if (factories == null || slot.fleetAstroId <= 0 || slot.fleetAstroId >= factories.Length)
                return SlotState.Unreachable;
            fleets = factories[slot.fleetAstroId]?.combatGroundSystem?.fleets;
        }
        if (fleets?.buffer == null) return SlotState.Unreachable;
        // Cursor alone does not guarantee capacity: a replicated pool can be reset and grown later.
        return slot.fleetId < fleets.cursor && slot.fleetId < fleets.buffer.Length &&
            fleets.buffer[slot.fleetId].id == slot.fleetId ? SlotState.Live : SlotState.Stale;
    }

    private static void Report(CombatModuleComponent module, string path, int repaired, int skipped)
    {
        if (repaired == 0 && skipped == 0) return;
        Diagnostics.Warn("fleet:slot:" + path + (module.isSpace ? ":space" : ":ground"), () =>
            $"[fleet] {path} order skipped a fleet key the local pool cannot resolve" +
            $" sphere={(module.isSpace ? "space" : "ground")} count={module.fleetCount} slots={module.moduleFleets?.Length}" +
            $" repaired={repaired} skipped={skipped}");
    }
}
