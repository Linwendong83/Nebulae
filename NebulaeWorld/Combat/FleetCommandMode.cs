namespace NebulaeWorld.Combat;

/// <summary>The deployed-fleet branch of vanilla UIZS_FleetEntry.OnCommandButtonClick.</summary>
internal static class FleetCommandMode
{
    public static bool Apply(CombatModuleComponent module, int index, bool inCommand)
    {
        if (module == null || index < 0 || index >= module.fleetCount) return false;
        // Native bulk/keyboard controls also set the mode of enabled empty slots.
        if (module.moduleFleets[index].fleetId == 0)
        { module.moduleFleets[index].inCommand = inCommand; return true; }
        if (!TryGetPool(module, index, out var fleets)) return false;
        ref var slot = ref module.moduleFleets[index];
        ref var fleet = ref fleets.buffer[slot.fleetId];
        slot.inCommand = inCommand;
        if (inCommand) fleet.dispatch = true;
        else fleet.CancelOrder();
        return true;
    }

    public static bool TryGetPool(CombatModuleComponent module, int index, out DataPool<FleetComponent> fleets)
    {
        fleets = null;
        if (module == null || index < 0 || index >= module.fleetCount || index >= module.moduleFleets.Length) return false;
        ref var slot = ref module.moduleFleets[index];
        fleets = module.isSpace ? GameMain.spaceSector?.combatSpaceSystem?.fleets :
            GameMain.galaxy?.PlanetById(slot.fleetAstroId)?.factory?.combatGroundSystem?.fleets;
        return fleets?.buffer != null && slot.fleetId > 0 && slot.fleetId < fleets.cursor &&
            slot.fleetId < fleets.buffer.Length && fleets.buffer[slot.fleetId].id == slot.fleetId;
    }
}
