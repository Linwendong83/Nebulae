#region

using NebulaeModel.DataStructures;
using UnityEngine;
using System;

#endregion

namespace NebulaeModel.Packets.Factory.Foundation;

[NebulaeModel.Networking.PacketId(5024598406586186607UL)]
internal class FoundationBuildUpdatePacket : NebulaeModel.Networking.IVegetationOperation
{
    public FoundationBuildUpdatePacket() { }

    public FoundationBuildUpdatePacket(PlanetFactory factory, BuildTool_Reform btr, Vector3 center, float radius,
        int reformSize, bool veinBuried, float fade0, bool isCircle, bool isRestore = false)
    {
        Radius = radius;
        ReformSize = reformSize;
        VeinBuried = veinBuried;
        Fade0 = fade0;
        IsRestore = isRestore;
        ReformType = btr?.brushType ?? -1;
        ReformColor = btr?.brushColor ?? -1;
        ReformMode = btr?.reformMode ?? 0;
        PlanetId = factory.planetId;
        GroundTestPos = new Float3(center);
        if (isCircle)
        {
            ReformIndices = btr?.extraCircleIndices == null ? Array.Empty<int>() : (int[])btr.extraCircleIndices.Clone();
            ExtraCenter = center.ToFloat3();
            CirclePointCount = btr?.circlePointCount ?? 0;
            IsCircle = true;
        }
        else //Normal reform
        {
            ReformIndices = btr?.cursorIndices == null ? Array.Empty<int>() : (int[])btr.cursorIndices.Clone();
            ExtraCenter = new Float3(Vector3.zero);
            CirclePointCount = 0;
            IsCircle = false;
        }
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public float Radius { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int ReformSize { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public bool VeinBuried { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public float Fade0 { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int ReformType { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int ReformColor { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int ReformMode { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public int[] ReformIndices { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public Float3 GroundTestPos { get; set; }
    [NebulaeModel.Networking.WireOrder(10)]
    public Float3 ExtraCenter { get; set; }
    [NebulaeModel.Networking.WireOrder(11)]
    public int CirclePointCount { get; set; }
    [NebulaeModel.Networking.WireOrder(12)]
    public bool IsCircle { get; set; }
    [NebulaeModel.Networking.WireOrder(13)]
    public bool IsRestore { get; set; }
    [NebulaeModel.Networking.WireOrder(14)]
    public byte[] CollectionBefore { get; set; } = Array.Empty<byte>();
    [NebulaeModel.Networking.WireOrder(15)]
    public int PitRuinId { get; set; }
    [NebulaeModel.Networking.WireOrder(16)]
    public long CommandSequence { get; set; }
    [NebulaeModel.Networking.WireOrder(17)]
    public long PitRuinGeneration { get; set; }
}
