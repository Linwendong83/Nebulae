#region

using System.Diagnostics.CodeAnalysis;
using NebulaeModel.DataStructures;

#endregion

namespace NebulaeModel.Packets.Logistics;

// this one is only used when a client connects to sync all existing gStations.
// needed because we also need to tell clients about ships that are already flying
[SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Field Name")]
[NebulaeModel.Networking.PacketId(12171453784054234019UL)]
internal class ILSgStationPoolSync
{
    public ILSgStationPoolSync() { }

    public ILSgStationPoolSync(int[] stationGId,
        int[] stationMaxShipCount,
        int[] stationId,
        string[] stationName,
        Float3[] DockPos,
        Float4[] DockRot,
        int[] planetId,
        int[] workShipCount,
        int[] idleShipCount,
        ulong[] workShipIndices,
        ulong[] idleShipIndices,
        int[] shipStage,
        int[] shipDirection,
        float[] shipWarpState,
        int[] shipWarperCnt,
        int[] shipItemID,
        int[] shipItemCount,
        int[] shipPlanetA,
        int[] shipPlanetB,
        int[] shipOtherGId,
        float[] shipT,
        int[] shipIndex,
        Double3[] shipPos,
        Float4[] shipRot,
        Float3[] shipVel,
        float[] shipSpeed,
        Float3[] shipAngularVel,
        Double3[] shipPPosTemp,
        Float4[] shipPRotTemp)
    {
        this.stationGId = stationGId;
        this.stationMaxShipCount = stationMaxShipCount;
        this.stationId = stationId;
        this.stationName = stationName;
        this.DockPos = DockPos;
        this.DockRot = DockRot;
        this.planetId = planetId;
        this.workShipCount = workShipCount;
        this.idleShipCount = idleShipCount;
        this.workShipIndices = workShipIndices;
        this.idleShipIndices = idleShipIndices;
        this.shipStage = shipStage;
        this.shipDirection = shipDirection;
        this.shipWarpState = shipWarpState;
        this.shipWarperCnt = shipWarperCnt;
        this.shipItemID = shipItemID;
        this.shipItemCount = shipItemCount;
        this.shipPlanetA = shipPlanetA;
        this.shipPlanetB = shipPlanetB;
        this.shipOtherGId = shipOtherGId;
        this.shipT = shipT;
        this.shipIndex = shipIndex;
        this.shipPos = shipPos;
        this.shipRot = shipRot;
        this.shipVel = shipVel;
        this.shipSpeed = shipSpeed;
        this.shipAngularVel = shipAngularVel;
        this.shipPPosTemp = shipPPosTemp;
        this.shipPRotTemp = shipPRotTemp;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int[] stationGId { get; set; }

    [NebulaeModel.Networking.WireOrder(1)]
    public int[] stationMaxShipCount { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int[] stationId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public string[] stationName { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public Float3[] DockPos { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public Float4[] DockRot { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int[] planetId { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public int[] workShipCount { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public int[] idleShipCount { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public ulong[] workShipIndices { get; set; }
    [NebulaeModel.Networking.WireOrder(10)]
    public ulong[] idleShipIndices { get; set; }
    [NebulaeModel.Networking.WireOrder(11)]
    public int[] shipStage { get; set; }
    [NebulaeModel.Networking.WireOrder(12)]
    public int[] shipDirection { get; set; }
    [NebulaeModel.Networking.WireOrder(13)]
    public float[] shipWarpState { get; set; }
    [NebulaeModel.Networking.WireOrder(14)]
    public int[] shipWarperCnt { get; set; }
    [NebulaeModel.Networking.WireOrder(15)]
    public int[] shipItemID { get; set; }
    [NebulaeModel.Networking.WireOrder(16)]
    public int[] shipItemCount { get; set; }
    [NebulaeModel.Networking.WireOrder(17)]
    public int[] shipPlanetA { get; set; }
    [NebulaeModel.Networking.WireOrder(18)]
    public int[] shipPlanetB { get; set; }
    [NebulaeModel.Networking.WireOrder(19)]
    public int[] shipOtherGId { get; set; }
    [NebulaeModel.Networking.WireOrder(20)]
    public float[] shipT { get; set; }
    [NebulaeModel.Networking.WireOrder(21)]
    public int[] shipIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(22)]
    public Double3[] shipPos { get; set; }
    [NebulaeModel.Networking.WireOrder(23)]
    public Float4[] shipRot { get; set; }
    [NebulaeModel.Networking.WireOrder(24)]
    public Float3[] shipVel { get; set; }
    [NebulaeModel.Networking.WireOrder(25)]
    public float[] shipSpeed { get; set; }
    [NebulaeModel.Networking.WireOrder(26)]
    public Float3[] shipAngularVel { get; set; }
    [NebulaeModel.Networking.WireOrder(27)]
    public Double3[] shipPPosTemp { get; set; }
    [NebulaeModel.Networking.WireOrder(28)]
    public Float4[] shipPRotTemp { get; set; }
    [NebulaeModel.Networking.WireOrder(29)]
    public long[] Revision { get; set; }
    [NebulaeModel.Networking.WireOrder(30)]
    public long[] Energy { get; set; }
    [NebulaeModel.Networking.WireOrder(31)]
    public int[] WarperCount { get; set; }
    [NebulaeModel.Networking.WireOrder(32)]
    public int[] ShipInc { get; set; }
    [NebulaeModel.Networking.WireOrder(33)]
    public int[] ShipGene { get; set; }
    [NebulaeModel.Networking.WireOrder(34)]
    public int MissingGId { get; set; }
    [NebulaeModel.Networking.WireOrder(35)]
    public float[] ShipAngularSpeed { get; set; }
    [NebulaeModel.Networking.WireOrder(36)]
    public double[] WarpEnableDistance { get; set; }

    public bool IsConsistent()
    {
        if (stationGId == null) return false;
        var count = stationGId.Length;
        System.Array[] stationArrays = [stationMaxShipCount, stationId, DockPos, DockRot, planetId,
            workShipCount, idleShipCount, workShipIndices, idleShipIndices, Revision, Energy, WarperCount, WarpEnableDistance];
        foreach (var array in stationArrays) if (array == null || array.Length != count) return false;
        var ships = 0;
        for (var i = 0; i < count; i++)
        {
            if (stationGId[i] <= 0 || planetId[i] <= 0 || Revision[i] <= 0 ||
                stationMaxShipCount[i] < 0 || stationMaxShipCount[i] > 64 || workShipCount[i] < 0 ||
                workShipCount[i] > stationMaxShipCount[i] || idleShipCount[i] < 0 ||
                idleShipCount[i] > stationMaxShipCount[i] - workShipCount[i]) return false;
            ships += workShipCount[i];
        }
        System.Array[] shipArrays = [shipStage, shipDirection, shipWarpState, shipWarperCnt, shipItemID,
            shipItemCount, shipPlanetA, shipPlanetB, shipOtherGId, shipT, shipIndex, shipPos, shipRot, shipVel,
            shipSpeed, shipAngularVel, shipPPosTemp, shipPRotTemp, ShipInc, ShipGene, ShipAngularSpeed];
        foreach (var array in shipArrays) if (array == null || array.Length != ships) return false;
        for (var i = 0; i < shipIndex.Length; i++) if (shipIndex[i] < 0 || shipIndex[i] >= 64) return false;
        return true;
    }
}
