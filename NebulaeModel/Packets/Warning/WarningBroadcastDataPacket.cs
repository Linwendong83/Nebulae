using NebulaeModel.DataStructures;
using UnityEngine;

namespace NebulaeModel.Packets.Warning;

[NebulaeModel.Networking.PacketId(15157471354079643875UL)]
internal class WarningBroadcastDataPacket
{
    public WarningBroadcastDataPacket() { }
    public WarningBroadcastDataPacket(EBroadcastVocal vocal, int astroId, int content, in Vector3 lpos)
    {
        Vocal = (short)vocal;
        AstroId = astroId;
        Content = content;
        Lpos = new Float3(lpos);
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public short Vocal { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int AstroId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int Content { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public Float3 Lpos { get; set; }
}
