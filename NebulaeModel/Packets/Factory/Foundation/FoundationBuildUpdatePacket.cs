#region

using NebulaeModel.DataStructures;
using UnityEngine;
using System;

#endregion

namespace NebulaeModel.Packets.Factory.Foundation;

[NebulaeModel.Networking.PacketId(5024598406586186607UL)]
internal class FoundationBuildUpdatePacket
{
    public FoundationBuildUpdatePacket() { }

    public FoundationBuildUpdatePacket(Vector3 center, float radius, int reformSize, bool veinBuried, float fade0,
        bool isRestore = false)
    {
        Radius = radius;
        ReformSize = reformSize;
        VeinBuried = veinBuried;
        Fade0 = fade0;
        IsRestore = isRestore;
        CollectionBefore = VegetableCollectionState.Capture(GameMain.mainPlayer?.vegetableCollection);
        //Assume FlattenTerrainReform are all called in BuildTool_Reform
        var btr = GameMain.mainPlayer.controller.actionBuild.reformTool;
        ReformType = btr?.brushType ?? -1;
        ReformColor = btr?.brushColor ?? -1;
        ReformMode = btr?.reformMode ?? 0;
        PlanetId = GameMain.mainPlayer.planetId;
        GroundTestPos = new Float3(btr?.castGroundPos ?? Vector3.zero);
        if (center != btr?.reformCenterPoint) //Pit (circle)
        {
            ReformIndices = btr?.extraCircleIndices;
            ExtraCenter = center.ToFloat3();
            CirclePointCount = btr.circlePointCount;
            IsCircle = true;
        }
        else //Normal reform
        {
            ReformIndices = btr?.cursorIndices;
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
}
