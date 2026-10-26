namespace NebulaeModel.Packets.GameStates;

[NebulaeModel.Networking.PacketId(3386484553756881438UL)]
internal class GameStateSaveInfoPacket
{
    public GameStateSaveInfoPacket() { }

    public GameStateSaveInfoPacket(long lastSaveTime)
    {
        LastSaveTime = lastSaveTime;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public long LastSaveTime { get; set; }
}
