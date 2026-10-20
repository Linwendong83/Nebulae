using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NebulaeModel.DataStructures;

// Configuration and identity only. Inventory and simulation outcomes are separate streams.
internal sealed class FacilityDirectoryEntry
{
    public int PlanetId, EntityId, Id, GId, ProtoId;
    public EControlPanelEntryType Type;
    public string Name = "", Tags = "", Comment = "";
    public int[] Items = Array.Empty<int>(), LocalModes = Array.Empty<int>(), RemoteModes = Array.Empty<int>();
    public int PlayerMode, StorageMode;

    public bool Matches(in ControlPanelFilter filter)
    {
        if (((int)filter.typeFilter & (1 << (int)Type)) == 0) return false;
        var search = filter.searchFilter;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var byId = int.TryParse(search, out var id) && id == Id;
            if (Type == EControlPanelEntryType.Dispenser)
            { if (int.TryParse(search, out _) && !byId) return false; }
            else if (!byId && !Contains(Name, search) && !(Type == EControlPanelEntryType.Marker && Contains(Comment, search))) return false;
        }
        if (Type == EControlPanelEntryType.Marker)
            return string.IsNullOrWhiteSpace(filter.tagFilter) || Tags.Split(';').Intersect(filter.tagFilter.Split(';')).Any();
        if (Type == EControlPanelEntryType.Dispenser)
        {
            var item = Items.Length == 0 ? 0 : Items[0];
            return (!filter.hasItemFilter || filter.ItemFilterPass(item) || (item < 0 && PlayerMode == (int)EPlayerDeliveryMode.Recycle)) &&
                (filter.stateFilter == -1 || (StorageMode > 0 && (filter.stateFilter == 0 || filter.stateFilter == StorageMode)) ||
                 (PlayerMode > 0 && (uint)(filter.stateFilter - PlayerMode - 9) <= 2u));
        }
        if (!filter.hasItemFilter && filter.stateFilter == -1) return true;
        var local = (filter.typeFilter & (ControlPanelFilter.EEntryFilter.LocalStation | ControlPanelFilter.EEntryFilter.VeinCollector)) != 0;
        var modes = local ? LocalModes : RemoteModes;
        for (var i = 0; i < Items.Length; i++)
            if ((!filter.hasItemFilter || filter.ItemFilterPass(Items[i])) &&
                (filter.stateFilter == -1 || (Items[i] > 0 && modes[i] == filter.stateFilter))) return true;
        return false;
    }

    private static bool Contains(string value, string search) => value?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

    public void Write(BinaryWriter w)
    {
        w.Write(PlanetId); w.Write(EntityId); w.Write(Id); w.Write(GId); w.Write(ProtoId); w.Write((int)Type);
        w.Write(Name ?? ""); w.Write(Tags ?? ""); w.Write(Comment ?? "");
        w.Write(PlayerMode); w.Write(StorageMode); w.Write(Items.Length);
        for (var i = 0; i < Items.Length; i++) { w.Write(Items[i]); w.Write(LocalModes[i]); w.Write(RemoteModes[i]); }
    }

    public static FacilityDirectoryEntry Read(BinaryReader r)
    {
        var entry = new FacilityDirectoryEntry
        {
            PlanetId = r.ReadInt32(),
            EntityId = r.ReadInt32(),
            Id = r.ReadInt32(),
            GId = r.ReadInt32(),
            ProtoId = r.ReadInt32(),
            Type = (EControlPanelEntryType)r.ReadInt32(),
            Name = r.ReadString(),
            Tags = r.ReadString(),
            Comment = r.ReadString(),
            PlayerMode = r.ReadInt32(),
            StorageMode = r.ReadInt32()
        };
        var count = r.ReadInt32();
        if (count < 0 || count > 256 || entry.PlanetId <= 0 || entry.EntityId <= 0 || entry.Type <= 0 || (int)entry.Type >= 7)
            throw new InvalidDataException("Invalid facility directory entry");
        entry.Items = new int[count]; entry.LocalModes = new int[count]; entry.RemoteModes = new int[count];
        for (var i = 0; i < count; i++) { entry.Items[i] = r.ReadInt32(); entry.LocalModes[i] = r.ReadInt32(); entry.RemoteModes[i] = r.ReadInt32(); }
        return entry;
    }
}
