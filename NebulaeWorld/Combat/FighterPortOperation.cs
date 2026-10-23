using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;

namespace NebulaeWorld.Combat;

/// <summary>Native port gestures, committed atomically with their hand and hangar transfers.</summary>
internal static class FighterPortOperation
{
    public const int Left = 0, Right = 1, Configure = 2, Control = 4, Space = 8;

    internal static int UnlockedPorts(ModuleFleet fleet, int capacity, bool space)
    {
        if (!space) return Math.Min(capacity, fleet.fighters.Length);
        var used = 0;
        for (var i = 0; i < fleet.fighters.Length; i++)
        {
            used += fleet.fighters[i].size == ECraftSize.Large ? 4 : 1;
            if (used > capacity) return i;
        }
        return fleet.fighters.Length;
    }

    internal static StorageComponent Clone(StorageComponent source)
    {
        var copy = new StorageComponent(source.size) { type = source.type, bans = source.bans };
        Array.Copy(source.grids, copy.grids, source.grids.Length);
        return copy;
    }

    public static bool TryApply(ref ModuleFleet fleet, int unlocked, int port, bool space, int gesture,
        int configuredItem, StorageComponent hangar, StorageComponent package, MechaData hand,
        out List<PlayerInventoryTransferLedger.Transfer> packageCredits,
        Func<int, ECraftSize, bool, bool> validFighter = null)
    {
        packageCredits = [];
        if (fleet.fighters == null || port < 0 || port >= Math.Min(unlocked, fleet.fighters.Length) || hangar == null || package == null) return false;
        var working = fleet;
        working.fighters = (ModuleFighter[])fleet.fighters.Clone();
        var stock = Clone(hangar); var bag = Clone(package);
        var held = new MechaData { HandItem = hand.HandItem, HandCount = hand.HandCount, HandInc = hand.HandInc };
        var credits = new List<PlayerInventoryTransferLedger.Transfer>();
        validFighter ??= ValidFighter;
        var count = Math.Min(unlocked, working.fighters.Length);
        var selected = working.fighters[port];
        var action = gesture & 3; var control = (gesture & Control) != 0;

        bool Put(int index)
        {
            ref var fighter = ref working.fighters[index];
            if (held.HandCount <= 0 || fighter.count > 0 || fighter.craftId > 0 || !validFighter(held.HandItem, fighter.size, space)) return false;
            var item = held.HandItem;
            if (held.TakeHand(item, 1, out _) != 1) return false;
            fighter.itemId = item; fighter.count = 1; return true;
        }
        bool Take(int index)
        {
            ref var fighter = ref working.fighters[index];
            if (fighter.count <= 0) return true;
            if (working.fleetId > 0 || fighter.craftId > 0 || !held.CreditHand(fighter.itemId, fighter.count, 0)) return false;
            fighter.count = 0; return true;
        }
        bool Store(int item, int quantity, int inc)
        {
            if (quantity <= 0) return true;
            var added = stock.AddItemStacked(item, quantity, inc, out var remainder);
            if (added < quantity)
            {
                var rest = quantity - added;
                if (bag.AddItemStacked(item, rest, remainder, out _) != rest) return false;
                credits.Add(new PlayerInventoryTransferLedger.Transfer(item, rest, remainder));
            }
            for (var i = 0; i < stock.size; i++) stock.SetFilter(i, stock.grids[i].itemId);
            return true;
        }
        bool Stow(int index)
        {
            ref var fighter = ref working.fighters[index];
            if (working.fleetId > 0 || fighter.craftId > 0) return false;
            if (!Store(fighter.itemId, fighter.count, 0)) return false;
            fighter.count = 0; return true;
        }

        if (action == Configure)
        {
            if (space || selected.count != 0 || selected.craftId != 0 || configuredItem != 0 && !validFighter(configuredItem, selected.size, false)) return false;
            working.fighters[port].itemId = configuredItem;
        }
        else if (action == Right)
        {
            if (held.HandCount > 0)
            {
                if (!Store(held.HandItem, held.HandCount, held.HandInc)) return false;
                held.HandItem = held.HandCount = held.HandInc = 0;
            }
            else
            {
                if (!Stow(port)) return false;
                if (!space) working.fighters[port].itemId = 0;
            }
        }
        else if (action == Left)
        {
            if (held.HandCount == 0)
            {
                if (selected.count > 0)
                {
                    for (var i = 0; i < count; i++)
                        if ((control ? working.fighters[i].itemId == selected.itemId : i == port) && !Take(i)) return false;
                }
                else if (control)
                {
                    for (var i = 0; i < count; i++) if (!Stow(i)) return false;
                }
            }
            else if (selected.count > 0)
            {
                if (working.fleetId > 0 || selected.craftId > 0) return false;
                if (selected.itemId == held.HandItem)
                {
                    for (var i = 0; i < count; i++)
                        if ((control ? working.fighters[i].itemId == selected.itemId : i == port) && !Take(i)) return false;
                }
                else if (held.HandCount == 1)
                {
                    var old = selected;
                    working.fighters[port].count = 0;
                    if (!Put(port) || !held.CreditHand(old.itemId, old.count, 0)) return false;
                }
            }
            else
            {
                var item = held.HandItem;
                if (!control)
                {
                    if (!Put(port)) return false;
                }
                else for (var i = 0; i < count && held.HandCount > 0; i++)
                        if (working.fighters[i].count == 0 && (working.fighters[i].itemId == 0 || working.fighters[i].itemId == item))
                            Put(i);
            }
        }
        else return false;

        Array.Copy(working.fighters, fleet.fighters, fleet.fighters.Length);
        MechaFightData.UpdateStorage(hangar, stock);
        MechaFightData.UpdateStorage(package, bag);
        hand.HandItem = held.HandItem; hand.HandCount = held.HandCount; hand.HandInc = held.HandInc;
        packageCredits = credits.GroupBy(credit => credit.Item)
            .Select(group => new PlayerInventoryTransferLedger.Transfer(group.Key, group.Sum(credit => credit.Count), group.Sum(credit => credit.Inc))).ToList();
        return true;
    }

    private static bool ValidFighter(int item, ECraftSize size, bool space)
    {
        var proto = LDB.items.Select(item);
        if (proto == null) return false;
        return space ? size == ECraftSize.Small ? proto.isSmallSpaceFighter : proto.isLargeSpaceFighter : proto.isGroundFighter;
    }
}
