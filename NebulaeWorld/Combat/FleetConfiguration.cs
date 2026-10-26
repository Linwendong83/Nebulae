using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;

namespace NebulaeWorld.Combat;

internal static class FleetConfiguration
{
    public static bool IsCurrent(CombatModuleComponent module, int index, int item)
    {
        if (module?.moduleFleets == null || index < 0 || index >= module.fleetCount || index >= module.moduleFleets.Length) return false;
        var fleet = module.moduleFleets[index];
        if (module.isSpace) return fleet.protoId == item;
        if (fleet.fighters == null) return false;
        foreach (var fighter in fleet.fighters) if (fighter.itemId != item) return false;
        return true;
    }
    public static bool TryPrepare(CombatModuleComponent module, int index)
    {
        if (module?.moduleFleets == null || index < 0 || index >= module.fleetCount ||
            index >= module.moduleFleets.Length || module.moduleFleets[index].fighters == null) return false;
        ref var fleet = ref module.moduleFleets[index];
        if (fleet.fleetId != 0)
        {
            // RecycleFleet requests return while any units are still deployed. Never replace
            // their port layout, or clear references to craft which are still in the world.
            if (!FleetCommandMode.TryGetPool(module, index, out _)) return false;
            module.RecycleFleet(index);
        }
        if (fleet.fleetId != 0) return false;
        foreach (var fighter in fleet.fighters) if (fighter.craftId != 0) return false;
        return true;
    }

    public static bool TryConfigureGround(CombatModuleComponent module, int index, int item,
        StorageComponent storage, StorageComponent package, out List<PlayerInventoryTransferLedger.Transfer> credits,
        System.Func<int, bool> validFighter = null)
    {
        credits = new();
        if (module == null || module.isSpace || storage == null || package == null ||
            !(validFighter ?? (id => LDB.items.Select(id)?.isGroundFighter == true))(item)) return false;
        if (IsCurrent(module, index, item)) return true;
        if (!TryPrepare(module, index)) return false;
        var originals = new List<StorageComponent>();
        var copies = new List<StorageComponent>();
        for (var current = storage; current != null; current = current.nextStorage)
        {
            if (originals.Contains(current)) return false;
            originals.Add(current); copies.Add(FighterPortOperation.Clone(current));
        }
        var bag = FighterPortOperation.Clone(package);
        ref var fleet = ref module.moduleFleets[index];
        var fighters = (ModuleFighter[])fleet.fighters.Clone();
        for (var i = 0; i < fighters.Length; i++)
        {
            ref var fighter = ref fighters[i];
            if (fighter.itemId == item) continue;
            var remaining = fighter.count;
            foreach (var copy in copies)
            {
                if (remaining <= 0) break;
                remaining -= copy.AddItemStacked(fighter.itemId, remaining, 0, out _);
            }
            if (remaining > 0)
            {
                if (bag.AddItemStacked(fighter.itemId, remaining, 0, out _) != remaining) return false;
                credits.Add(new PlayerInventoryTransferLedger.Transfer(fighter.itemId, remaining, 0));
            }
            fighter.itemId = item; fighter.count = 0;
        }
        for (var i = 0; i < copies.Count; i++) MechaFightData.UpdateStorage(originals[i], copies[i]);
        MechaFightData.UpdateStorage(package, bag);
        System.Array.Copy(fighters, fleet.fighters, fighters.Length);
        return true;
    }
}
