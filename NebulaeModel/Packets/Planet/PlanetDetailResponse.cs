namespace NebulaeModel.Packets.Planet;

[NebulaeModel.Networking.PacketId(15849901618009321242UL)]
internal class PlanetDetailResponse
{
    public PlanetDetailResponse() { }

    public PlanetDetailResponse(int planetId, in VeinGroup[] veinGroups, float landPercent)
    {
        PlanetDataID = planetId;
        VeinTypes = new byte[veinGroups.Length];
        VeinCounts = new int[veinGroups.Length];
        VeinAmounts = new long[veinGroups.Length];
        for (var i = 1; i < veinGroups.Length; i++)
        {
            VeinTypes[i] = (byte)veinGroups[i].type;
            VeinCounts[i] = veinGroups[i].count;
            VeinAmounts[i] = veinGroups[i].amount;
        }
        LandPercent = landPercent;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetDataID { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public byte[] VeinTypes { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int[] VeinCounts { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public long[] VeinAmounts { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public float LandPercent { get; set; }
    [NebulaeModel.Networking.WireOrder(5)] public bool Success { get; set; } = true;
}
