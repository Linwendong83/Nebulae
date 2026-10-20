#region

using NebulaeModel.Networking;
using NebulaeModel.DataStructures;

#endregion

namespace NebulaeModel.Packets.Universe;

[NebulaeModel.Networking.PacketId(12867809242726088181UL)]
internal class DysonLaunchDataPacket
{
    public DysonLaunchDataPacket() { }

    public DysonLaunchDataPacket(DysonLaunchData data)
    {
        Data = data;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public DysonLaunchData Data { get; set; }
}
