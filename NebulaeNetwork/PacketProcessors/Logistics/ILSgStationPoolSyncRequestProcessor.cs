#region

using System.Collections.Generic;
using System.Linq;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics;
using NebulaeWorld;

#endregion

/*
 * Whenever a client connects we sync the current state of all ILS and ships to them
 * resulting in a quite large packet but its only sent one time upon client connect.
 */
namespace NebulaeNetwork.PacketProcessors.Logistics;
internal class ILSgStationPoolSyncRequestProcessor : PacketProcessor<ILSRequestgStationPoolSync>
{
    protected override void ProcessPacket(ILSRequestgStationPoolSync packet, NebulaeConnection conn)
    {
        if (IsClient)
        {
            return;
        }

        if (packet.GId < 0) return;
        var gid = packet.GId; // The packet instance is reused by the serializer.
        Multiplayer.Session.Ships.QueueSnapshot(() => SendSnapshot(gid, conn));
    }

    internal static void SendSnapshot(int gid, NebulaeConnection conn)
    {
        var iter = 0;
        var stations = GameMain.data.galacticTransport.stationPool.Where(station => station != null &&
            (gid == 0 || station.gid == gid)).ToArray();
        var countILS = stations.Length;

        var stationGId = new int[countILS];
        var stationMaxShipCount = new int[countILS];
        var stationId = new int[countILS];
        var stationName = new string[countILS];
        var DockPos = new Float3[countILS];
        var DockRot = new Float4[countILS];
        var planetId = new int[countILS];
        var workShipCount = new int[countILS];
        var idleShipCount = new int[countILS];
        var workShipIndices = new ulong[countILS];
        var idleShipIndices = new ulong[countILS];
        var revisions = new long[countILS];
        var energy = new long[countILS];
        var warpers = new int[countILS];
        var warpDistance = new double[countILS];
        var shipInc = new List<int>();
        var shipGene = new List<int>();
        var shipAngularSpeed = new List<float>();

        var shipStage = new List<int>();
        var shipDirection = new List<int>();
        var shipWarpState = new List<float>();
        var shipWarperCnt = new List<int>();
        var shipItemID = new List<int>();
        var shipItemCount = new List<int>();
        var shipPlanetA = new List<int>();
        var shipPlanetB = new List<int>();
        var shipOtherGId = new List<int>();
        var shipT = new List<float>();
        var shipIndex = new List<int>();
        var shipPos = new List<Double3>();
        var shipRot = new List<Float4>();
        var shipVel = new List<Float3>();
        var shipSpeed = new List<float>();
        var shipAngularVel = new List<Float3>();
        var shipPPosTemp = new List<Double3>();
        var shipPRotTemp = new List<Float4>();

        foreach (var stationComponent in stations)
        {
            if (stationComponent == null)
            {
                continue;
            }
            stationGId[iter] = stationComponent.gid;
            stationMaxShipCount[iter] = stationComponent.workShipDatas.Length;
            stationId[iter] = stationComponent.id;
            DockPos[iter] = new Float3(stationComponent.shipDockPos);
            DockRot[iter] = new Float4(stationComponent.shipDockRot);
            planetId[iter] = stationComponent.planetId;
            workShipCount[iter] = stationComponent.workShipCount;
            idleShipCount[iter] = stationComponent.idleShipCount;
            workShipIndices[iter] = stationComponent.workShipIndices;
            idleShipIndices[iter] = stationComponent.idleShipIndices;
            revisions[iter] = Multiplayer.Session.Ships.Lifecycle.SnapshotRevision(stationComponent);
            energy[iter] = stationComponent.energy;
            warpers[iter] = stationComponent.warperCount;
            warpDistance[iter] = stationComponent.warpEnableDist;

            // ShipData is never null
            for (var ship = 0; ship < stationComponent.workShipCount; ship++)
            {
                var shipData = stationComponent.workShipDatas[ship];
                shipInc.Add(shipData.inc);
                shipGene.Add(shipData.gene);
                shipAngularSpeed.Add(shipData.uAngularSpeed);
                shipStage.Add(shipData.stage);
                shipDirection.Add(shipData.direction);
                shipWarpState.Add(shipData.warpState);
                shipWarperCnt.Add(shipData.warperCnt);
                shipItemID.Add(shipData.itemId);
                shipItemCount.Add(shipData.itemCount);
                shipPlanetA.Add(shipData.planetA);
                shipPlanetB.Add(shipData.planetB);
                shipOtherGId.Add(shipData.otherGId);
                shipT.Add(shipData.t);
                shipIndex.Add(shipData.shipIndex);
                shipPos.Add(new Double3(shipData.uPos.x, shipData.uPos.y, shipData.uPos.z));
                shipRot.Add(new Float4(shipData.uRot));
                shipVel.Add(new Float3(shipData.uVel));
                shipSpeed.Add(shipData.uSpeed);
                shipAngularVel.Add(new Float3(shipData.uAngularVel));
                shipPPosTemp.Add(new Double3(shipData.pPosTemp.x, shipData.pPosTemp.y, shipData.pPosTemp.z));
                shipPRotTemp.Add(new Float4(shipData.pRotTemp));
            }

            iter++;
        }

        var packet2 = new ILSgStationPoolSync(
            stationGId,
            stationMaxShipCount,
            stationId,
            stationName,
            DockPos,
            DockRot,
            planetId,
            workShipCount,
            idleShipCount,
            workShipIndices,
            idleShipIndices,
            shipStage.ToArray(),
            shipDirection.ToArray(),
            shipWarpState.ToArray(),
            shipWarperCnt.ToArray(),
            shipItemID.ToArray(),
            shipItemCount.ToArray(),
            shipPlanetA.ToArray(),
            shipPlanetB.ToArray(),
            shipOtherGId.ToArray(),
            shipT.ToArray(),
            shipIndex.ToArray(),
            shipPos.ToArray(),
            shipRot.ToArray(),
            shipVel.ToArray(),
            shipSpeed.ToArray(),
            shipAngularVel.ToArray(),
            shipPPosTemp.ToArray(),
            shipPRotTemp.ToArray())
        {
            Revision = revisions,
            Energy = energy,
            WarperCount = warpers,
            WarpEnableDistance = warpDistance,
            ShipInc = shipInc.ToArray(),
            ShipGene = shipGene.ToArray(),
            ShipAngularSpeed = shipAngularSpeed.ToArray(),
            MissingGId = countILS == 0 ? gid : 0
        };
        conn.SendPacket(packet2);
    }
}
