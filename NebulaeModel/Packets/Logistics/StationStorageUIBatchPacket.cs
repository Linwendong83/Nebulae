#region

#endregion

namespace NebulaeModel.Packets.Logistics;

/// <summary>Several storage slots of one station in a single packet.</summary>
/// <remarks>Parallel arrays carry slot contents and drone, ship and warper settings, so one station interaction
/// produces one message.</remarks>
[NebulaeModel.Networking.PacketId(9210644338157720391UL)]
internal class StationStorageUIBatchPacket
{
    /// <summary>Ceiling on the slots one packet may carry, matching the largest station in the game.</summary>
    public const int MaxStorageEntries = 64;

    public StationStorageUIBatchPacket() { }

    /// <param name="storageIdx">Slot index of every updated storage entry.</param>
    /// <param name="itemCount">Absolute item count per slot, parallel to <paramref name="storageIdx" />.</param>
    /// <param name="itemInc">Item increment per slot, parallel to <paramref name="storageIdx" />.</param>
    /// <param name="settingIndex">Station settings applied after the slots, as <see cref="StationUI.EUISettings" />.</param>
    /// <param name="settingValue">Setting value per entry, parallel to <paramref name="settingIndex" />.</param>
    public StationStorageUIBatchPacket(int planetId, int stationId, int stationGId, int[] storageIdx, int[] itemCount,
        int[] itemInc, byte[] settingIndex, int[] settingValue)
    {
        PlanetId = planetId;
        StationId = stationId;
        StationGId = stationGId;
        StorageIdx = storageIdx;
        ItemCount = itemCount;
        ItemInc = itemInc;
        SettingIndex = settingIndex;
        SettingValue = settingValue;
    }

    /// <summary>True only on the copy the host sends back to the player who asked, which is the one allowed to refund.</summary>
    [NebulaeModel.Networking.WireOrder(8)]
    public bool ShouldRefund { get; set; }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }

    [NebulaeModel.Networking.WireOrder(1)]
    public int StationId { get; set; }

    [NebulaeModel.Networking.WireOrder(2)]
    public int StationGId { get; set; }

    [NebulaeModel.Networking.WireOrder(3)]
    public int[] StorageIdx { get; set; }

    [NebulaeModel.Networking.WireOrder(4)]
    public int[] ItemCount { get; set; }

    [NebulaeModel.Networking.WireOrder(5)]
    public int[] ItemInc { get; set; }

    [NebulaeModel.Networking.WireOrder(6)]
    public byte[] SettingIndex { get; set; }

    [NebulaeModel.Networking.WireOrder(7)]
    public int[] SettingValue { get; set; }

    /// <summary>
    ///     The arrays must line up and stay inside the station, or the packet describes nothing the
    ///     receiver can apply. Checked before anything is written.
    /// </summary>
    public bool IsConsistent()
    {
        return StorageIdx != null && ItemCount != null && ItemInc != null &&
               StorageIdx.Length == ItemCount.Length && StorageIdx.Length == ItemInc.Length &&
               StorageIdx.Length <= MaxStorageEntries &&
               (SettingIndex == null || SettingValue == null
                   ? SettingIndex == null && SettingValue == null
                   : SettingIndex.Length == SettingValue.Length && SettingIndex.Length <= 8);
    }
}
