namespace NebulaeModel.Packets.GameHistory;

[NebulaeModel.Networking.PacketId(7817556361069280764UL)]
internal class GameHistoryResearchUpdatePacket
{
    public GameHistoryResearchUpdatePacket() { }

    public GameHistoryResearchUpdatePacket(int techId, long hashUploaded, long hashNeeded, int techHashedFor10Frames,
        int techQueueLength) : this(techId, hashUploaded, hashNeeded, techHashedFor10Frames, techQueueLength, false)
    {
    }

    public GameHistoryResearchUpdatePacket(int techId, long hashUploaded, long hashNeeded, int techHashedFor10Frames,
        int techQueueLength, bool hasActiveAutomaticResearch)
    {
        TechId = techId;
        HashUploaded = hashUploaded;
        HashNeeded = hashNeeded;
        TechHashedFor10Frames = techHashedFor10Frames;
        TechQueueLength = (ushort)techQueueLength;
        HasActiveAutomaticResearch = hasActiveAutomaticResearch;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int TechId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public long HashUploaded { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public long HashNeeded { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int TechHashedFor10Frames { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public ushort TechQueueLength { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public bool HasActiveAutomaticResearch { get; set; }
}
