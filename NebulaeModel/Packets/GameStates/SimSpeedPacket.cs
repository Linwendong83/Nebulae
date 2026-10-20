#region

#endregion

namespace NebulaeModel.Packets.GameStates;

[NebulaeModel.Networking.PacketId(11735822291897248203UL)]
internal class SimSpeedPacket
{
    public SimSpeedPacket() { }

    public SimSpeedPacket(float speed)
    {
        Speed = speed;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public float Speed { get; set; }
}
