#region

using NebulaeModel.DataStructures;
using UnityEngine;

#endregion

namespace NebulaeModel.Packets.Universe.Editor;

[NebulaeModel.Networking.PacketId(4893346313559228375UL)]
internal class DysonSpherePaintCellsPacket
{
    public DysonSpherePaintCellsPacket() { }

    public DysonSpherePaintCellsPacket(int starIndex, int layerId, Color32 paint, float strength, bool superBrightMode,
        int[] cursorCells, int cellCount)
    {
        StarIndex = starIndex;
        LayerId = layerId;
        Paint = Float4.ToFloat4(paint);
        Strength = strength;
        SuperBrightMode = superBrightMode;
        CursorCells = cursorCells;
        CellCount = cellCount;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int LayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public Float4 Paint { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public float Strength { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public bool SuperBrightMode { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int[] CursorCells { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int CellCount { get; set; }
}
