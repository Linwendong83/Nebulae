#region

#endregion

namespace NebulaeModel.Packets.GameStates;

[NebulaeModel.Networking.PacketId(2059223239710366120UL)]
internal class GamePausePacket
{
    public GamePausePacket() { }

    public GamePausePacket(bool paused)
    {
        Paused = paused;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public bool Paused { get; set; }
}
