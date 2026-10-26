#region

using System;
using System.Collections.Generic;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Factory;
using NebulaeWorld.Factory;
using UnityEngine;
using Random = UnityEngine.Random;
#pragma warning disable IDE1006 // Naming Styles

#endregion

namespace NebulaeWorld.Player;

internal class DroneManager : IDisposable
{
    private readonly Dictionary<ushort, PlayerPosition> cachedPositions = [];
    private Vector3[] localPlayerPos = new Vector3[2];
    private int localPlayerCount = 0;
    private long lastCheckedTick = 0;
    private readonly List<CraftData> crafts = [];
    private readonly Stack<int> craftRecyleIds = [];
    private readonly DataPool<DroneComponent> drones = new();
    private readonly Dictionary<long, int> repairIds = new();
    private readonly Dictionary<int, (long Sequence, DroneFlightView View)> repairPoses = new();
    private readonly HashSet<long> retiredRepairs = new();
    private readonly Queue<long> retiredRepairOrder = new();

    public DroneManager()
    {
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public void EjectMechaDroneFromOtherPlayer(ushort playerId, int planetId, int targetObjectId,
        int next1ObjectId, int next2ObjectId, int next3ObjectId, int dronePriority)
    {
        if (targetObjectId > 0 || ConstructionDroneLife.IsDead(playerId)) return; // Repair views are driven by authoritative flight samples.
        RefreshCachedPositions();

        var ejectPos = GetPlayerEjectPosition(playerId);
        var factory = GameMain.galaxy.PlanetById(planetId).factory;
        var targetPos = factory.constructionSystem._obj_hpos(targetObjectId, ref ejectPos);
        var initialVector = ejectPos + ejectPos.normalized * 4.5f +
                      ((targetPos - ejectPos).normalized + Random.insideUnitSphere) * 1.5f;
        // Use custom CreateDrone to store in separate pool
        ref var ptr = ref CreateDrone(factory, ejectPos, Quaternion.LookRotation(initialVector), Vector3.zero, playerId);
        ptr.stage = 1;
        ptr.priority = dronePriority;
        ptr.targetObjectId = targetObjectId;
        ptr.nextTarget1ObjectId = next1ObjectId;
        ptr.nextTarget2ObjectId = next2ObjectId;
        ptr.nextTarget3ObjectId = next3ObjectId;
        ptr.targetPos = targetPos;
        ptr.initialVector = initialVector;
        ptr.progress = 0f;
        ptr.movement = factory.gameData.history.constructionDroneMovement;
        ptr.owner = planetId; // Use drone.owner field to store planetId

    }

    public void RefreshCachedPositions()
    {
        if (GameMain.gameTick != lastCheckedTick)
        {
            lastCheckedTick = GameMain.gameTick;
            localPlayerCount = 0;
            var currentLocalPlanetId = GameMain.localPlanet?.id ?? int.MinValue;

            using (Multiplayer.Session.World.GetRemotePlayersModels(out var remotePlayersModels))
            {
                // host needs it for all players since they can build on other planets too.
                foreach (var model in remotePlayersModels.Values)
                {
                    var playerPos = model.Movement.GetLastPosition().LocalPlanetPosition.ToVector3();
                    var ejectPos = playerPos.normalized * (playerPos.magnitude + 2.8f);
                    var localPlanetId = model.Movement.localPlanetId;

                    // Cache players positions
                    if (cachedPositions.TryGetValue(model.Movement.PlayerID, out var playerPosition))
                    {
                        playerPosition.Position = ejectPos;
                        playerPosition.PlanetId = localPlanetId;
                    }
                    else
                    {
                        cachedPositions.Add(model.Movement.PlayerID, new PlayerPosition(ejectPos, model.Movement.localPlanetId));
                    }

                    if (currentLocalPlanetId != localPlanetId) continue;
                    if (localPlayerCount >= localPlayerPos.Length)
                    {
                        var newArray = new Vector3[localPlayerPos.Length * 2];
                        Array.Copy(localPlayerPos, newArray, localPlayerPos.Length);
                        localPlayerPos = newArray;
                    }
                    localPlayerPos[localPlayerCount++] = playerPos;
                }
            }
        }
    }

    public Vector3 GetPlayerEjectPosition(ushort playerId)
    {
        return cachedPositions.TryGetValue(playerId, out var value) ? value.Position : GameMain.mainPlayer.position;
    }

    public void ClearAllRemoteDrones()
    {
        crafts.Clear();
        craftRecyleIds.Clear();
        drones.Reset();
        repairIds.Clear(); repairPoses.Clear();
        retiredRepairs.Clear(); retiredRepairOrder.Clear();
    }

    public void ApplyRepairPose(RepairDroneStatePacket packet)
    {
        if (retiredRepairs.Contains(packet.Flight)) return;
        if (ConstructionDroneLife.IsDead(packet.PlayerId)) { RetireRepair(packet.Flight); return; }
        var factory = GameMain.galaxy?.PlanetById(packet.PlanetId)?.factory;
        if (factory == null || !DroneFlightState.TryDecode(packet.FlightState, out var state)) return;
        if (repairIds.TryGetValue(packet.Flight, out var id))
        {
            if (repairPoses.TryGetValue(id, out var previous) && previous.Sequence >= packet.Sequence) return;
            previous.View.Apply(state);
            repairPoses[id] = (packet.Sequence, previous.View);
            return;
        }
        else
        {
            if (packet.Stage == 0) return;
            ref var drone = ref CreateDrone(factory, new Vector3(packet.X, packet.Y, packet.Z),
                new Quaternion(packet.Qx, packet.Qy, packet.Qz, packet.Qw), Vector3.zero, packet.PlayerId);
            id = drone.id; drone.owner = packet.PlanetId; drone.stage = packet.Stage;
            repairIds[packet.Flight] = id;
        }
        repairPoses[id] = (packet.Sequence, new DroneFlightView(state));
    }

    public void RecyclePlayerDrones(ushort playerId)
    {
        for (var id = 1; id < drones.cursor; id++)
        {
            ref var drone = ref drones.buffer[id];
            if (drone.id != id || crafts[drone.craftId].owner != playerId) continue;
            RecycleDrone(GameMain.galaxy?.PlanetById(drone.owner)?.factory, ref drone);
        }
    }

    public void CancelRemoteBuildTarget(PlanetFactory factory, ushort playerId, int prebuildId)
    {
        if (factory == null || prebuildId <= 0) return;
        for (var id = 1; id < drones.cursor; id++)
        {
            ref var drone = ref drones.buffer[id];
            if (drone.id != id || drone.owner != factory.planetId ||
                crafts[drone.craftId].owner != playerId ||
                drone.targetObjectId != -prebuildId && drone.nextTarget1ObjectId != -prebuildId &&
                drone.nextTarget2ObjectId != -prebuildId && drone.nextTarget3ObjectId != -prebuildId)
                continue;
            RecycleDrone(factory, ref drone);
        }
    }

    public void UpdateDrones(PlanetFactory factory, ObjectRenderer[] renderers, bool sync_gpu_inst, float dt)
    {
        // Mimic from ConstructionSystem.UpdateDrones
        var constructionDroneSpeed = factory.gameData.history.constructionDroneSpeed;
        var planetId = factory.planetId;

        for (var droneId = 1; droneId < drones.cursor; droneId++)
        {
            ref var ptr = ref drones.buffer[droneId];
            if (ptr.owner != planetId) //ptr.owner is planetId in the custom pool
            {
                continue;
            }
            if (ptr.id != droneId || ptr.stage == 0) continue;
            var craftData = crafts[ptr.craftId];
            var playerId = (ushort)craftData.owner;
            if (ConstructionDroneLife.IsDead(playerId))
            {
                RecyclePlayerDrones(playerId);
                continue;
            }
            if (repairPoses.TryGetValue(droneId, out var flight))
            {
                RefreshCachedPositions();
                var eject = cachedPositions.TryGetValue(playerId, out var owner) && owner.PlanetId == planetId
                    ? owner.Position : flight.View.Command.EjectPosition;
                flight.View.Advance(factory, eject, dt);
                if (flight.View.Finished)
                {
                    RecycleDrone(factory, ref ptr);
                    continue;
                }
                craftData.pos = flight.View.Craft.pos; craftData.rot = flight.View.Craft.rot;
                ptr.stage = flight.View.Drone.stage; crafts[ptr.craftId] = craftData;
                if (sync_gpu_inst) { factory.planet.factoryModel.craftDirty = true; UpdateGpuInstance(renderers, in craftData, ptr.stage); }
                continue;
            }
            RefreshCachedPositions();
            if (!cachedPositions.TryGetValue(playerId, out var playerPosition) || playerPosition.PlanetId != planetId)
            {
                // If the owner leave the planet, recycle the drone
                RecycleDrone(factory, ref ptr);
                continue;
            }

            // Update drone stage and craft position
            var ejectPos = playerPosition.Position;
            var meachEnerey = (double)float.MaxValue; // Dummy value for remote drones
            var mecahEnergyChange = 0.0;
            var result = ptr.InternalUpdate(ref craftData, factory, ref ejectPos, constructionDroneSpeed, dt,
                ref meachEnerey, ref mecahEnergyChange, 0, 0, out _);
            crafts[ptr.craftId] = craftData;

            // Repair or find the next target
            UpdateDroneStageAndTarget(factory, ref ptr, result);

            if (sync_gpu_inst)
            {
                // Vanilla only marks craftDirty for its own drone pool. Remote drones live
                // here, so their updated instance positions otherwise stay on the CPU.
                factory.planet.factoryModel.craftDirty = true;
                UpdateGpuInstance(renderers, in craftData, ptr.stage);
            }
        }
    }

    internal void UpdateDroneStageAndTarget(PlanetFactory factory, ref DroneComponent ptr, int result)
    {
        // DroneComponent.stage [1]:eject from player [2]:going to target [3]:on target [4]:back to player
        if (ptr.stage == 3)
        {
            if (ptr.targetObjectId > 0) // Repair entity
            {
                // Legacy launch-only replicas never execute repair work or reserve a worker.
                ptr.targetObjectId = 0; result = 1;
            }
            else if (result == 0 && ptr.targetObjectId < 0)
            {
                var prebuild = -ptr.targetObjectId;
                // Stay at the site until its authoritative construction/removal reaches this world.
                if (prebuild >= factory.prebuildPool.Length || factory.prebuildPool[prebuild].id != prebuild ||
                    factory.prebuildPool[prebuild].isDestroyed) result = 1;
            }
        }
        if (result == 1 && ptr.stage == 4)
        {
            RecycleDrone(factory, ref ptr);
        }
        if (result != 0 && (ptr.stage == 2 || ptr.stage == 3 || ptr.stage == 4))
        {
            ptr.movement--;
            if (ptr.movement <= 0)
            {
                ptr.movement = 0;
                ptr.stage = 4;
                ptr.targetObjectId = 0;
            }
            else if (ptr.nextTarget1ObjectId != 0)
            {
                ptr.stage = 2;
                ptr.targetObjectId = ptr.nextTarget1ObjectId;
                ptr.targetPos = factory.constructionSystem._obj_hpos(ptr.nextTarget1ObjectId);
                ptr.nextTarget1ObjectId = 0;
            }
            else if (ptr.nextTarget2ObjectId != 0)
            {
                ptr.stage = 2;
                ptr.targetObjectId = ptr.nextTarget2ObjectId;
                ptr.targetPos = factory.constructionSystem._obj_hpos(ptr.nextTarget2ObjectId);
                ptr.nextTarget2ObjectId = 0;
            }
            else if (ptr.nextTarget3ObjectId != 0)
            {
                ptr.stage = 2;
                ptr.targetObjectId = ptr.nextTarget3ObjectId;
                ptr.targetPos = factory.constructionSystem._obj_hpos(ptr.nextTarget3ObjectId);
                ptr.nextTarget3ObjectId = 0;
            }
            else
            {
                ptr.stage = 4;
                ptr.targetObjectId = 0;
            }
        }
    }

    internal static void UpdateGpuInstance(ObjectRenderer[] renderers, in CraftData craftData, int droneStage)
    {
        if (renderers != null && craftData.modelIndex >= 0 && craftData.modelIndex < renderers.Length &&
            craftData.modelId > 0 && renderers[craftData.modelIndex] is DynamicRenderer dynamicRenderer && craftData.modelId < dynamicRenderer.instPool.Length)
        {
            var instPool = dynamicRenderer.instPool;
            var modelId = craftData.modelId;
            instPool[modelId].posx = (float)craftData.pos.x;
            instPool[modelId].posy = (float)craftData.pos.y;
            instPool[modelId].posz = (float)craftData.pos.z;
            instPool[modelId].rotx = craftData.rot.x;
            instPool[modelId].roty = craftData.rot.y;
            instPool[modelId].rotz = craftData.rot.z;
            instPool[modelId].rotw = craftData.rot.w;
            dynamicRenderer.extraPool[craftData.modelId].x = droneStage;
        }
    }

    private ref DroneComponent CreateDrone(PlanetFactory factory, Vector3 pos, Quaternion rot, Vector3 vel, ushort playerId)
    {
        int craftId;
        if (craftRecyleIds.Count > 0)
        {
            craftId = craftRecyleIds.Pop();
        }
        else
        {
            crafts.Add(default);
            craftId = crafts.Count - 1;
        }

        var craftData = default(CraftData);
        craftData.id = craftId;
        craftData.protoId = 0;
        craftData.modelIndex = 454;
        craftData.astroId = factory.planetId;
        craftData.owner = playerId; // Use craft.owner field to store playerId
        craftData.port = 0;
        craftData.prototype = ECraftProto.ConstructionDrone;
        craftData.dynamic = true;
        craftData.isSpace = false;
        craftData.pos = pos;
        craftData.rot = rot;
        craftData.vel = vel;
        var planet = factory.planet;
        if (planet.factoryLoaded || planet.factingCompletedStage >= 5)
        {
            craftData.modelId = GameMain.gpuiManager.AddModel(454, craftId, pos, rot, true);
        }
        crafts[craftId] = craftData;

        ref var drone = ref drones.Add();
        drone.craftId = craftId;

        return ref drone;
    }

    private void RecycleDrone(PlanetFactory factory, ref DroneComponent dronePtr)
    {
        // These are display-only drones. They never own a native repair reservation.
        var droneId = dronePtr.id;
        repairPoses.Remove(droneId);
        foreach (var token in new List<long>(repairIds.Keys))
        {
            if (repairIds[token] != droneId) continue;
            repairIds.Remove(token);
            RetireRepair(token);
            break;
        }

        var craftId = dronePtr.craftId;
        var modelId = crafts[craftId].modelId;
        if (modelId != 0 && factory != null && GameMain.gpuiManager.activeFactory == factory)
        {
            GameMain.gpuiManager.RemoveModel(454, modelId, true);
        }
        crafts[craftId].SetEmpty();
        craftRecyleIds.Push(craftId);
        drones.Remove(dronePtr.id);
    }

    private void RetireRepair(long token)
    {
        if (retiredRepairs.Add(token)) retiredRepairOrder.Enqueue(token);
        if (retiredRepairOrder.Count > 4096) retiredRepairs.Remove(retiredRepairOrder.Dequeue());
    }
}
