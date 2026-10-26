namespace NebulaeModel.Packets.GameStates;

[NebulaeModel.Networking.PacketId(10534716064951593226UL)]
internal class GameStateUpdate
{
    public GameStateUpdate() { }

    public GameStateUpdate(long sentTime, long gameTick, float unitsPerSecond, float simSpeed)
    {
        SentTime = sentTime;
        GameTick = gameTick;
        UnitsPerSecond = unitsPerSecond;
        SimSpeed = simSpeed;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public long SentTime { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public long GameTick { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public float UnitsPerSecond { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public float SimSpeed { get; set; }
}
