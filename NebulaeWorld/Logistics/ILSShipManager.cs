#region

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NebulaeModel.DataStructures;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Logistics;
using UnityEngine;

#endregion

namespace NebulaeWorld.Logistics;

internal class ILSShipManager
{
    public readonly ToggleSwitch PatchLockILS = new();

    // the following 4 are needed to prevent a packet flood when the filter on a belt connected to a PLS/ILS is set.
    public int ItemSlotLastSelectedIndex = 0;
    public int ItemSlotLastSlotId = 0;
    public int ItemSlotStationGId = 0;
    public int ItemSlotStationId = 0;

    public readonly ShipLifecycle Lifecycle = new();
    public readonly StationStorageJournal Inventory = new();
    private readonly ConditionalWeakTable<StationStore[], StationComponent> storageOwners = new();
    private readonly Queue<Action> snapshots = new();

    public void QueueSnapshot(Action capture)
    {
        lock (snapshots) snapshots.Enqueue(capture);
    }

    // Called only while simulation workers are parked, so each baseline fences a complete batch.
    public void FlushLifecycle()
    {
        Lifecycle.Flush(GameMain.data.galacticTransport.stationPool, packet => Multiplayer.Session.Network.SendPacket(packet));
        Inventory.Flush(GameMain.data.galacticTransport.stationPool, packet =>
            Multiplayer.Session.Network.SendPacketToStar(packet, packet.PlanetId / 100));
        while (true)
        {
            Action capture;
            lock (snapshots)
            {
                if (snapshots.Count == 0) return;
                capture = snapshots.Dequeue();
            }
            capture();
        }
    }

    public void ReceiveLifecycle(ILSShipLifecyclePacket packet)
    {
        var pool = GameMain.data.galacticTransport.stationPool;
        var station = packet.GId > 0 && packet.GId < pool.Length ? pool[packet.GId] : null;
        if (!Lifecycle.Receive(packet, station))
        {
            if (Lifecycle.NeedsRepair(packet.GId))
                Multiplayer.Session.Network.SendPacket(new ILSRequestgStationPoolSync { GId = packet.GId });
            return;
        }
        EnsureDestinations(station);
    }

    public void RegisterStorage(StationComponent station)
    {
        if (station?.storage == null || storageOwners.TryGetValue(station.storage, out _)) return;
        lock (storageOwners)
            if (!storageOwners.TryGetValue(station.storage, out _)) storageOwners.Add(station.storage, station);
    }

    public void RecordStorageDelta(StationStore[] storage, int index, int delta, bool increment)
    {
        if (index < 0 || index >= storage.Length || !storageOwners.TryGetValue(storage, out var station)) return;
        Inventory.Record(station, index, storage[index].itemId, increment ? 0 : delta, increment ? delta : 0);
    }

    public void ReceiveStorage(ILSStorageCommitPacket packet)
    {
        var pool = GameMain.data.galacticTransport.stationPool;
        var station = packet.GId > 0 && packet.GId < pool.Length ? pool[packet.GId] : null;
        if (!Inventory.Receive(packet, station) && Inventory.NeedsRepair(packet.GId))
            Multiplayer.Session.Network.SendPacket(new ILSArriveStarPlanetRequest(packet.PlanetId / 100) { GId = packet.GId });
    }

    // A newly launched ship may target a factory the client has never loaded.
    public static void EnsureDestinations(StationComponent station)
    {
        if (station == null) return;
        for (var i = 0; i < station.workShipCount; i++)
        {
            var ship = station.workShipDatas[i];
            var pool = GameMain.data.galacticTransport.stationPool;
            if (ship.otherGId > 0 && (ship.otherGId >= pool.Length || pool[ship.otherGId] == null))
                CreateFakeStationComponent(ship.otherGId, ship.planetB, station.workShipDatas.Length);
        }
    }
    /*
     * Create an entry in the gStationPool with minimal info for ships to travel and render correctly.
     * The information is needed in StationComponent.InternalTickRemote(), but we use a reverse patched version of that
     * which is stripped down to the ship movement and rendering part.
     */
    public static void CreateFakeStationComponent(int gId, int planetId, int maxShipCount, bool computeDisk = true)
    {
        // it may be needed to make additional room for the new ILS
        while (GameMain.data.galacticTransport.stationPool.Length <= gId)
        {
            GameMain.data.galacticTransport.SetStationCapacity(GameMain.data.galacticTransport.stationPool.Length * 2);
        }


        GameMain.data.galacticTransport.stationPool[gId] = new StationComponent();
        var stationComponent = GameMain.data.galacticTransport.stationPool[gId];
        stationComponent.isStellar = true;
        var planet = GameMain.galaxy?.PlanetById(planetId);
        stationComponent.isCollector = planet == null ? false : planet.type == EPlanetType.Gas; // assume station on gas giant are all orbital collectors
        stationComponent.gid = gId;
        stationComponent.planetId = planetId;
        stationComponent.workShipDatas = new ShipData[maxShipCount];
        stationComponent.workShipOrders = new RemoteLogisticOrder[maxShipCount];
        stationComponent.shipRenderers = new ShipRenderingData[maxShipCount];
        stationComponent.shipUIRenderers = new ShipUIRenderingData[maxShipCount];
        stationComponent.priorityLocks = new StationPriorityLock[6];
        stationComponent.workShipCount = 0;
        stationComponent.idleShipCount = maxShipCount; // add dummy idle ship count to use in ILSShipManager
        stationComponent.shipDockPos = Vector3.zero; //gets updated later by server packet
        stationComponent.shipDockRot = Quaternion.identity; // gets updated later by server packet
        stationComponent.storage = []; // zero-length array for mod compatibility
        if (computeDisk)
        {
            stationComponent.shipDiskPos = new Vector3[maxShipCount];
            stationComponent.shipDiskRot = new Quaternion[maxShipCount];

            for (var i = 0; i < maxShipCount; i++)
            {
                stationComponent.shipDiskRot[i] = Quaternion.Euler(0f, 360f / maxShipCount * i, 0f);
                stationComponent.shipDiskPos[i] = stationComponent.shipDiskRot[i] * new Vector3(0f, 0f, 11.5f);
            }
            for (var j = 0; j < maxShipCount; j++)
            {
                stationComponent.shipDiskRot[j] = stationComponent.shipDockRot * stationComponent.shipDiskRot[j];
                stationComponent.shipDiskPos[j] = stationComponent.shipDockPos +
                                                  stationComponent.shipDockRot * stationComponent.shipDiskPos[j];
            }

            if (Multiplayer.Session.Ships.Lifecycle.NeedsRepair(gId))
                Multiplayer.Session.Network.SendPacket(new ILSRequestgStationPoolSync { GId = gId });
        }

        GameMain.data.galacticTransport.stationCursor = Math.Max(GameMain.data.galacticTransport.stationCursor, gId + 1);
    }

    public static void UpdateSlotData(ILSUpdateSlotData packet)
    {
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        StationComponent stationComponent = null;

        if (factory != null) // only update station slot for loaded factories
        {
            stationComponent = factory.transport.stationPool[packet.StationId];
        }

        if (stationComponent?.slots == null)
        {
            return;
        }
        stationComponent.slots[packet.Index].storageIdx = packet.StorageIdx;
        if (stationComponent.gid != packet.StationGId)
        {
            Log.Warn($"Station gid mismatch! local:{stationComponent.gid} remote:{packet.StationGId}");
        }
    }
}
