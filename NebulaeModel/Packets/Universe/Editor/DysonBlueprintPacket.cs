#region

using System.Text;

#endregion

namespace NebulaeModel.Packets.Universe.Editor;

[NebulaeModel.Networking.PacketId(10947547252292020254UL)]
internal class DysonBlueprintPacket
{
    public DysonBlueprintPacket() { }

    public DysonBlueprintPacket(int starIndex, int layerId, EDysonBlueprintType blueprintType, string stringData)
    {
        StarIndex = starIndex;
        LayerId = layerId;
        BlueprintType = blueprintType;
        // because string length may exceed maxStringLength in NetSerializer, convert to char array here
        BinaryData = Encoding.ASCII.GetBytes(stringData);
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int LayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public EDysonBlueprintType BlueprintType { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public byte[] BinaryData { get; set; }
}
