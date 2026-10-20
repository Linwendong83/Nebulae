namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(14736307002007341227UL)]
internal class ILSArriveStarPlanetResponse
{
    public ILSArriveStarPlanetResponse() { }

    public ILSArriveStarPlanetResponse(int[] stationGId,
        int[] planetId,
        int[] stationMaxShips,
        int[] storageLength,
        int[] storageIdx,
        int[] slotLength,
        int[] itemId,
        int[] count,
        int[] inc)
    {
        StationGId = stationGId;
        StationPId = planetId;
        StationMaxShips = stationMaxShips;
        StorageLength = storageLength;
        StorageIdx = storageIdx;
        SlotLength = slotLength;
        ItemId = itemId;
        Count = count;
        Inc = inc;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int[] StationGId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int[] StationPId { get; set; }

    [NebulaeModel.Networking.WireOrder(2)]
    public int[] StationMaxShips { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int[] StorageLength { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int[] SlotLength { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int[] StorageIdx { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int[] ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public int[] Count { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public int[] Inc { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public long[] Revision { get; set; }
    [NebulaeModel.Networking.WireOrder(10)]
    public int MissingGId { get; set; }

    public bool IsConsistent()
    {
        if (StationGId == null) return false;
        System.Array[] rows = [StationPId, StationMaxShips, StorageLength, SlotLength, Revision];
        foreach (var row in rows) if (row == null || row.Length != StationGId.Length) return false;
        var storage = 0; var slots = 0;
        for (var i = 0; i < StationGId.Length; i++)
        {
            if (StationGId[i] <= 0 || StationPId[i] <= 0 || Revision[i] <= 0 || StationMaxShips[i] < 0 || StationMaxShips[i] > 64 ||
                StorageLength[i] < 0 || StorageLength[i] > 64 || SlotLength[i] < 0 || SlotLength[i] > 64) return false;
            storage += StorageLength[i]; slots += SlotLength[i];
        }
        if (StorageIdx == null || StorageIdx.Length != slots || ItemId == null || ItemId.Length != storage ||
            Count == null || Count.Length != storage || Inc == null || Inc.Length != storage) return false;
        for (var i = 0; i < storage; i++) if (ItemId[i] < 0 || Count[i] < 0 || Inc[i] < 0) return false;
        return true;
    }
}
