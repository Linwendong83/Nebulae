using System.Collections.Generic;

namespace NebulaeModel.Packets.Factory.Foundation;

[NebulaeModel.Networking.PacketId(4323145100503440832UL)]
internal class FoundationBlueprintPastePacket
{
    public FoundationBlueprintPastePacket() { }

    public FoundationBlueprintPastePacket(int planetId, Dictionary<int, int> levelChanges,
        byte[] reformData)
    {
        PlanetId = planetId;
        LevelChangesKeys = new int[levelChanges.Count];
        LevelChangesValues = new int[levelChanges.Count];
        var index = 0;
        foreach (var pair in levelChanges)
        {
            LevelChangesKeys[index] = pair.Key;
            LevelChangesValues[index] = pair.Value;
            index++;
        }
        ReformData = reformData;
        ReformGridIds = [];
    }

    public FoundationBlueprintPastePacket(int planetId, Dictionary<int, int> levelChanges,
        int[] reformGridIds, int reformType, int reformColor)
    {
        PlanetId = planetId;
        LevelChangesKeys = new int[levelChanges.Count];
        LevelChangesValues = new int[levelChanges.Count];
        var index = 0;
        foreach (var pair in levelChanges)
        {
            LevelChangesKeys[index] = pair.Key;
            LevelChangesValues[index] = pair.Value;
            index++;
        }
        ReformData = [];
        ReformGridIds = reformGridIds;
        ReformType = reformType;
        ReformColor = reformColor;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int[] LevelChangesKeys { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int[] LevelChangesValues { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public byte[] ReformData { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int[] ReformGridIds { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int ReformType { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int ReformColor { get; set; }
}
