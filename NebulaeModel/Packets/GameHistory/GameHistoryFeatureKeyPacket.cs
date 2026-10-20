namespace NebulaeModel.Packets.GameHistory;

[NebulaeModel.Networking.PacketId(2007727300557766337UL)]
internal class GameHistoryFeatureKeyPacket
{
    public GameHistoryFeatureKeyPacket() { }

    public GameHistoryFeatureKeyPacket(int featureId, bool add)
    {
        FeatureId = featureId;
        Add = add;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int FeatureId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public bool Add { get; set; }
}
