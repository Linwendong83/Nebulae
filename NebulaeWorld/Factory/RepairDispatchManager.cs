using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Factory;
using UnityEngine;

namespace NebulaeWorld.Factory;

/// <summary>Repair demand, flight physics, real energy and HP are settled together on the host.</summary>
internal sealed class RepairDispatchManager : IDisposable
{
    private sealed class Flight
    {
        public long Id, Generation, Created, Seen, Simulated = -1;
        public ushort Player;
        public PlanetFactory Factory;
        public int Entity, Priority, NativeId;
        public int ResumeDrone, Remaining;
        public long OfferSequence;
        public bool Started, Returning, Native;
        public DroneComponent Drone;
        public CraftData Craft;
        public DroneFlightState? Published;
        public long PublishedGeneration;
        public float Power = 1;
    }
    private sealed class Binding
    {
        public long Id, Tick;
        public PlanetFactory Factory;
        public int Drone, Priority;
        public bool Canceling;
        public DroneFlightView View;
    }
    private readonly Dictionary<long, Flight> flights = new();
    private readonly Dictionary<long, Binding> bindings = new();
    private readonly Dictionary<(PlanetFactory Factory, int Drone), long> nativeBindings = new();
    private readonly Dictionary<(PlanetFactory Factory, int Drone), (long Offer, int Craft)> continuations = new();
    private readonly Dictionary<(ushort Player, int Planet), (RepairOfferPacket Offer, long Tick)> offers = new();
    private readonly Dictionary<(ushort Player, int Planet), long> sequences = new();
    private readonly Dictionary<int, (int Target, long Tick)> lastOffers = new();
    private readonly HashSet<long> ended = new();
    private readonly Queue<long> endedOrder = new();
    private RepairControlPacket launching;
    private long nextFlight, nextOffer, nextState;
    public int ActiveRemoteFlights => flights.Values.Count(f => !f.Native && !f.Returning);
    public int WorkingRemoteFlights => flights.Values.Count(f => !f.Native && !f.Returning && f.Started && f.Drone.stage == 3);
    public double EnergySpent { get; private set; }
    public int ResumedFlights { get; private set; }
    public bool HasRemoteRepairs(PlanetFactory factory) => flights.Values.Any(f => !f.Native && !f.Returning && f.Factory == factory);
    public void BeginFrame(PlanetFactory factory)
    {
        var stats = factory.constructionSystem.constructStats;
        for (var id = 1; id < stats.cursor; id++)
            if (stats.buffer[id].id == id) { stats.buffer[id].repairerModuleId = 0; stats.buffer[id].repairerValue = 0; }
        if (Multiplayer.Session.IsServer) Recount(factory);
    }

    public void Dispose() { flights.Clear(); bindings.Clear(); nativeBindings.Clear(); continuations.Clear(); offers.Clear(); ended.Clear(); endedOrder.Clear(); }
    private static Vector3 Eject(Vector3 position) => position.normalized * (position.magnitude + 2.8f);
    private static bool Target(PlanetFactory factory, int entity, long generation, out int stat)
    {
        stat = 0;
        if (factory == null || entity <= 0 || entity >= factory.entityCursor || factory.entityPool[entity].id != entity) return false;
        stat = factory.entityPool[entity].constructStatId;
        return ConstructionRepairTargets.Valid(factory, stat) &&
            Multiplayer.Session.AuthorityRuntime.FactoryCombatHost.TryGetEntityKey(factory, entity, out var key) && key.Generation == generation;
    }
    public void Offer(ConstructionModuleComponent module, PlanetFactory factory)
    {
        if (Multiplayer.Session.IsServer || module.entityId != 0 || module.droneIdleCount <= 0 || !module.droneRepairEnabled ||
            !module.droneEnabled || module.repairTargets == null || GameMain.mainPlayer.mecha.coreEnergy <= 0) return;
        foreach (var target in module.repairTargets)
        {
            var stat = target.objectId;
            if (stat <= 0 || !ConstructionRepairTargets.Valid(factory, stat)) continue;
            ref var repair = ref factory.constructionSystem.constructStats.buffer[stat];
            if (repair.repairerModuleId != -1) continue;
            var entity = repair.entityId;
            if (lastOffers.TryGetValue(factory.planetId, out var last) && last.Target == entity && GameMain.gameTick - last.Tick < 10) return;
            if (!Multiplayer.Session.AuthorityRuntime.WorldReplica.TryFindKey(PoolKind.Entity, factory.planetId, entity, out var key)) return;
            lastOffers[factory.planetId] = (entity, GameMain.gameTick);
            Multiplayer.Session.Network.SendPacket(new RepairOfferPacket
            {
                PlanetId = factory.planetId,
                EntityId = entity,
                Generation = key.Generation,
                Sequence = ++nextOffer,
                Idle = module.droneIdleCount,
                Priority = module.dronePriority
            });
            return;
        }
    }
    public void ReceiveOffer(ushort player, RepairOfferPacket packet)
    {
        if (packet.Sequence <= 0 || packet.Idle <= 0 || packet.Idle > 256 || packet.Priority < 0 || packet.Priority > 2 ||
            packet.ResumeDrone < 0 || packet.Remaining < 0 || packet.Remaining > 4 || !Finite(packet.X) || !Finite(packet.Y) || !Finite(packet.Z)) return;
        var key = (player, packet.PlanetId);
        if (sequences.TryGetValue(key, out var sequence) && packet.Sequence <= sequence) return;
        sequences[key] = packet.Sequence; offers[key] = (packet, GameMain.gameTick);
    }
    public void OfferContinuation(PlanetFactory factory, int droneId, int entity)
    {
        if (Multiplayer.Session.IsServer || droneId <= 0 || droneId >= factory.constructionSystem.drones.cursor) return;
        ref var drone = ref factory.constructionSystem.drones.buffer[droneId];
        var player = GameMain.mainPlayer; var module = player.mecha.constructionModule;
        if (drone.id != droneId || drone.owner != 0 || drone.movement <= 0 || nativeBindings.ContainsKey((factory, droneId)) ||
            !module.droneEnabled || !module.droneRepairEnabled || player.mecha.coreEnergy <= 0 ||
            !Multiplayer.Session.AuthorityRuntime.WorldReplica.TryFindKey(PoolKind.Entity, factory.planetId, entity, out var key)) return;
        var sequence = ++nextOffer; var pos = (Vector3)factory.craftPool[drone.craftId].pos;
        continuations[(factory, droneId)] = (sequence, drone.craftId);
        Multiplayer.Session.Network.SendPacket(new RepairOfferPacket
        {
            PlanetId = factory.planetId,
            EntityId = entity,
            Generation = key.Generation,
            Sequence = sequence,
            Idle = 1,
            Priority = module.dronePriority,
            ResumeDrone = droneId,
            Remaining = drone.movement,
            X = pos.x,
            Y = pos.y,
            Z = pos.z
        });
    }
    public void Prepare(PlanetFactory factory)
    {
        var system = factory.constructionSystem;
        if (!Multiplayer.Session.IsServer)
        {
            // The client's native PreLaunchDrone has already emitted its next repair offer.
            for (var id = 1; id < system.constructStats.cursor; id++)
                if (system.constructStats.buffer[id].id == id) system.constructStats.buffer[id].repairerModuleId = 0;
            return;
        }
        Recount(factory);
        var candidates = new List<(ushort Player, RepairOfferPacket Offer, float Score)>();
        foreach (var entry in offers.ToArray())
        {
            if (entry.Key.Planet != factory.planetId) continue;
            offers.Remove(entry.Key);
            var player = Multiplayer.Session.Server.Players.Get(entry.Key.Player);
            var mecha = Multiplayer.Session.CombatAuthority.MechaFor(entry.Key.Player);
            var offer = entry.Value.Offer;
            if (player?.Data.LocalPlanetId != factory.planetId || mecha?.player.isAlive != true || mecha.coreEnergy <= 0 ||
                GameMain.gameTick - entry.Value.Tick > 60 || !Target(factory, offer.EntityId, offer.Generation, out _) ||
                flights.Values.Any(f => f.Player == entry.Key.Player && !f.Started)) continue;
            if (offer.ResumeDrone > 0 && (offer.Remaining <= 0 ||
                (new Vector3(offer.X, offer.Y, offer.Z) - player.Data.LocalPlanetPosition.ToVector3()).sqrMagnitude > 14400)) continue;
            var score = BuildCandidateScore.Calculate(BuildOwnerKind.Player,
                (factory.entityPool[offer.EntityId].pos - player.Data.LocalPlanetPosition.ToVector3()).sqrMagnitude, mecha.buildArea);
            if (score > 0) candidates.Add((entry.Key.Player, offer, score));
        }
        foreach (var candidate in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Player))
        {
            var offer = candidate.Offer;
            if (!Target(factory, offer.EntityId, offer.Generation, out var stat)) continue;
            ref var repair = ref system.constructStats.buffer[stat];
            if (repair.repairerModuleId != 0 && repair.repairerValue >= candidate.Score) continue;
            ref var combat = ref factory.skillSystem.combatStats.buffer[factory.entityPool[offer.EntityId].combatStatId];
            var count = Math.Min(offer.Idle, RepairDemand.Count(combat.hp, combat.hpMax, system.GetRepairDroneDemand(ref repair, ref combat), repair.repairerCount));
            var total = Multiplayer.Session.CombatAuthority.MechaFor(candidate.Player)?.constructionModule.droneCount ?? 0;
            count = Math.Min(count, total - flights.Values.Count(f => f.Player == candidate.Player));
            for (var i = 0; i < count; i++)
            {
                var flight = new Flight
                {
                    Id = ++nextFlight,
                    Player = candidate.Player,
                    Factory = factory,
                    Entity = offer.EntityId,
                    Generation = offer.Generation,
                    Priority = offer.Priority,
                    Created = GameMain.gameTick,
                    Seen = GameMain.gameTick
                };
                flights[flight.Id] = flight;
                flight.ResumeDrone = offer.ResumeDrone; flight.Remaining = offer.Remaining; flight.OfferSequence = offer.Sequence;
                Multiplayer.Session.Server.Players.Get(candidate.Player).Connection.SendPacket(Control(flight, RepairControl.Grant));
                repair.repairerCount++;
            }
            if (count > 0) { repair.repairerModuleId = 0; repair.repairerValue = 0; }
        }
    }
    private static RepairControlPacket Control(Flight f, RepairControl message) => new()
    {
        Message = message,
        Flight = f.Id,
        PlanetId = f.Factory.planetId,
        EntityId = f.Entity,
        Generation = f.Generation,
        Priority = f.Priority,
        ResumeDrone = f.ResumeDrone,
        OfferSequence = f.OfferSequence
    };
    public void ReceiveControl(ushort player, RepairControlPacket packet)
    {
        if (!Multiplayer.Session.IsServer) { if (packet.Message == RepairControl.Grant) Grant(packet); return; }
        if (!flights.TryGetValue(packet.Flight, out var f) || f.Native || f.Player != player || f.Factory.planetId != packet.PlanetId) return;
        f.Seen = GameMain.gameTick;
        if (packet.Message == RepairControl.Cancel) { Return(f); return; }
        if (packet.Message != RepairControl.Started || f.Started || f.Returning) return;
        var actor = Multiplayer.Session.CombatAuthority.MechaFor(player);
        if (actor?.player.isAlive != true || actor.player.factory != f.Factory || !Target(f.Factory, f.Entity, f.Generation, out _)) { End(f); return; }
        if (!DroneFlightState.TryDecode(packet.FlightState, out var launched) || launched.Target != f.Entity ||
            launched.Stage != (f.ResumeDrone > 0 ? 2 : 1) ||
            (launched.Position - actor.player.position).sqrMagnitude > 14400 ||
            f.ResumeDrone == 0 && (launched.InitialVector - launched.Position).sqrMagnitude > 256)
        { End(f); return; }
        var eject = Eject(actor.player.position);
        var target = f.Factory.constructionSystem._obj_hpos(f.Entity, ref eject);
        f.Craft = new CraftData { pos = launched.Position, rot = launched.Rotation, owner = player };
        f.Drone = new DroneComponent
        {
            id = 1,
            stage = 1,
            owner = 0,
            targetObjectId = f.Entity,
            targetPos = target,
            initialVector = launched.InitialVector,
            movement = f.Factory.gameData.history.constructionDroneMovement,
            priority = f.Priority
        };
        f.Started = true;
        if (f.ResumeDrone > 0)
        {
            f.Drone.stage = 2;
            f.Drone.movement = Math.Min(f.Remaining, f.Factory.gameData.history.constructionDroneMovement);
            ResumedFlights++;
        }
        Publish(f);
    }
    private void Grant(RepairControlPacket packet)
    {
        if (packet.Flight <= 0 || ended.Contains(packet.Flight) || bindings.ContainsKey(packet.Flight)) return;
        var player = GameMain.mainPlayer; var factory = player?.factory; var module = player?.mecha.constructionModule;
        var accepted = player?.isAlive == true && factory?.planetId == packet.PlanetId && module?.droneEnabled == true && module.droneRepairEnabled &&
            (packet.ResumeDrone > 0 || module.droneIdleCount > 0) && module.dronePriority == packet.Priority && player.speed <= 20 && player.mecha.coreEnergy > 0 &&
            Multiplayer.Session.AuthorityRuntime.WorldReplica.TryFindKey(PoolKind.Entity, packet.PlanetId, packet.EntityId, out var key) && key.Generation == packet.Generation &&
            ConstructionRepairTargets.Valid(factory, factory.entityPool[packet.EntityId].constructStatId);
        if (accepted && packet.ResumeDrone > 0)
        {
            var pool = factory.constructionSystem.drones;
            accepted = continuations.TryGetValue((factory, packet.ResumeDrone), out var pending) && pending.Offer == packet.OfferSequence &&
                packet.ResumeDrone < pool.cursor && pool.buffer[packet.ResumeDrone].id == packet.ResumeDrone &&
                pool.buffer[packet.ResumeDrone].owner == 0 && pool.buffer[packet.ResumeDrone].stage == 4 && pool.buffer[packet.ResumeDrone].craftId == pending.Craft;
        }
        if (!accepted) { packet.Message = RepairControl.Cancel; Multiplayer.Session.Network.SendPacket(packet); RememberEnd(packet.Flight); return; }
        if (packet.ResumeDrone > 0)
        {
            continuations.Remove((factory, packet.ResumeDrone));
            var binding = new Binding { Id = packet.Flight, Factory = factory, Drone = packet.ResumeDrone, Priority = packet.Priority, Tick = -1 };
            bindings[binding.Id] = binding; nativeBindings[(factory, binding.Drone)] = binding.Id;
            ref var drone = ref factory.constructionSystem.drones.buffer[binding.Drone];
            drone.stage = 2; drone.targetObjectId = packet.EntityId; drone.targetPos = factory.constructionSystem._obj_hpos(packet.EntityId);
            var state = LocalFlight(factory, drone);
            binding.View = new DroneFlightView(state); packet.FlightState = state.Encode();
            packet.Message = RepairControl.Started; Multiplayer.Session.Network.SendPacket(packet); return;
        }
        launching = packet;
        try { module.EjectMechaDrone(factory, player, packet.EntityId, 0, 0, 0); }
        finally { launching = null; }
    }
    public void NativeLaunched(PlanetFactory factory, int entity)
    {
        var drones = factory.constructionSystem.drones;
        for (var id = drones.cursor - 1; id > 0; id--)
        {
            ref var drone = ref drones.buffer[id];
            if (drone.id != id || drone.owner != 0 || drone.stage != 1 || drone.targetObjectId != entity || nativeBindings.ContainsKey((factory, id))) continue;
            if (Multiplayer.Session.IsServer)
            {
                if (!Multiplayer.Session.AuthorityRuntime.FactoryCombatHost.TryGetEntityKey(factory, entity, out var key)) return;
                var f = new Flight
                {
                    Id = ++nextFlight,
                    Factory = factory,
                    Player = Multiplayer.Session.LocalPlayer.Id,
                    Entity = entity,
                    Generation = key.Generation,
                    Native = true,
                    NativeId = id,
                    Started = true
                };
                flights[f.Id] = f; nativeBindings[(factory, id)] = f.Id; Publish(f); return;
            }
            if (launching == null) return;
            var binding = new Binding { Id = launching.Flight, Factory = factory, Drone = id, Priority = launching.Priority, Tick = -1 };
            bindings[binding.Id] = binding; nativeBindings[(factory, id)] = binding.Id;
            var state = LocalFlight(factory, drone);
            binding.View = new DroneFlightView(state); launching.FlightState = state.Encode();
            launching.Message = RepairControl.Started; Multiplayer.Session.Network.SendPacket(launching); return;
        }
    }
    private static DroneFlightState LocalFlight(PlanetFactory factory, in DroneComponent drone) =>
        DroneFlightState.Capture(drone, factory.craftPool[drone.craftId], Eject(GameMain.mainPlayer.position),
            factory.gameData.history.constructionDroneSpeed, GameMain.mainPlayer.mecha.coreEnergy > 0);
    public bool IsReplica(PlanetFactory factory, int drone) => !Multiplayer.Session.IsServer && nativeBindings.ContainsKey((factory, drone));
    public void BeforeReset(PlanetFactory factory, int drone)
    {
        if (!nativeBindings.TryGetValue((factory, drone), out var token)) return;
        if (Multiplayer.Session.IsServer) { if (flights.TryGetValue(token, out var f)) f.Returning = true; return; }
        if (bindings.TryGetValue(token, out var binding)) Cancel(binding);
    }
    private static void Cancel(Binding b)
    {
        if (b.Canceling) return;
        b.Canceling = true;
        Multiplayer.Session.Network.SendPacket(new RepairControlPacket { Message = RepairControl.Cancel, Flight = b.Id, PlanetId = b.Factory.planetId });
    }
    public void Recycled(PlanetFactory factory, int drone)
    {
        continuations.Remove((factory, drone));
        if (!nativeBindings.TryGetValue((factory, drone), out var token)) return;
        nativeBindings.Remove((factory, drone));
        if (Multiplayer.Session.IsServer) { if (flights.TryGetValue(token, out var f)) End(f); }
        else if (bindings.TryGetValue(token, out var b)) { Cancel(b); bindings.Remove(token); RememberEnd(token); }
    }
    public void Tick()
    {
        if (!Multiplayer.Session.IsGameLoaded) return;
        if (!Multiplayer.Session.IsServer)
        {
            var player = GameMain.mainPlayer;
            foreach (var b in bindings.Values.ToArray())
            {
                var module = player.mecha.constructionModule;
                if (player.factory != b.Factory || !player.isAlive || !module.droneEnabled || !module.droneRepairEnabled ||
                    module.dronePriority == 1 && module.dronePriority != b.Priority)
                    Cancel(b);
                b.Priority = module.dronePriority;
                if (GameMain.gameTick % 15 == 0 && !b.Canceling)
                    Multiplayer.Session.Network.SendPacket(new RepairControlPacket { Message = RepairControl.Heartbeat, Flight = b.Id, PlanetId = b.Factory.planetId });
            }
            return;
        }
        foreach (var f in flights.Values.Where(f => f.Native).ToArray())
        {
            var pool = f.Factory.constructionSystem.drones;
            if (f.NativeId >= pool.cursor || pool.buffer[f.NativeId].id != f.NativeId || pool.buffer[f.NativeId].stage == 0) { End(f); continue; }
            ref var drone = ref pool.buffer[f.NativeId];
            if (drone.targetObjectId > 0 && drone.targetObjectId != f.Entity &&
                Multiplayer.Session.AuthorityRuntime.FactoryCombatHost.TryGetEntityKey(f.Factory, drone.targetObjectId, out var key))
            { f.Entity = drone.targetObjectId; f.Generation = key.Generation; }
            Publish(f);
        }
        // A native host construction flight can switch to repair without another eject call.
        var data = GameMain.data;
        for (var i = 0; i < data.factoryCount; i++)
        {
            var factory = data.factories[i]; if (factory == null) continue;
            var pool = factory.constructionSystem.drones;
            for (var id = 1; id < pool.cursor; id++)
            {
                ref var drone = ref pool.buffer[id];
                if (drone.id != id || drone.owner != 0 || drone.stage <= 0 || drone.stage >= 4 || drone.targetObjectId <= 0 || nativeBindings.ContainsKey((factory, id))) continue;
                if (!Multiplayer.Session.AuthorityRuntime.FactoryCombatHost.TryGetEntityKey(factory, drone.targetObjectId, out var key)) continue;
                var flight = new Flight
                {
                    Id = ++nextFlight,
                    Factory = factory,
                    Player = Multiplayer.Session.LocalPlayer.Id,
                    Entity = drone.targetObjectId,
                    Generation = key.Generation,
                    Native = true,
                    NativeId = id,
                    Started = true
                };
                flights[flight.Id] = flight; nativeBindings[(factory, id)] = flight.Id; Publish(flight);
            }
        }
    }
    public void TickFactory(PlanetFactory factory, long time)
    {
        foreach (var f in flights.Values.Where(f => !f.Native && f.Factory == factory).ToArray())
        {
            if (f.Simulated == time) continue;
            f.Simulated = time;
            var mecha = Multiplayer.Session.CombatAuthority.MechaFor(f.Player);
            if (mecha?.player.isAlive != true || mecha.player.factory != factory) { End(f); continue; }
            if (time - f.Seen > 120) Return(f);
            if (!f.Started) { if (time - f.Created >= 120) End(f); continue; }
            var player = mecha.player; var eject = Eject(player.position);
            if (!f.Returning && (f.Craft.pos - (VectorLF3)eject).sqrMagnitude > 14400) Return(f);
            if (!f.Returning && !Target(factory, f.Entity, f.Generation, out _))
            {
                f.Drone.movement--;
                if (f.Drone.movement <= 0 || !NextRepair(f, mecha)) Return(f);
            }
            var before = mecha.coreEnergy; var change = 0.0;
            var result = f.Drone.InternalUpdate(ref f.Craft, factory, ref eject, factory.gameData.history.constructionDroneSpeed, 1f / 60f,
                ref mecha.coreEnergy, ref change, Configs.freeMode.droneEnergyPerMeter,
                Configs.freeMode.droneRepairHpPerTick * Configs.freeMode.droneRepairEnergyCostPerHP, out var ratio);
            f.Power = mecha.coreEnergy > 500 ? ratio : 0;
            Multiplayer.Session.CombatAuthority.RecordRepairEnergy(f.Player, Math.Max(0, before - mecha.coreEnergy));
            EnergySpent += Math.Max(0, before - mecha.coreEnergy);
            if (f.Drone.stage == 3 && f.Drone.targetObjectId > 0 && factory.constructionSystem.Repair(f.Entity, ratio, time))
            { f.Drone.targetObjectId = 0; result = 1; }
            if (result == 1 && f.Drone.stage == 4) { End(f); continue; }
            if (result != 0 && (f.Drone.stage == 2 || f.Drone.stage == 3))
            {
                f.Drone.movement--;
                if (f.Returning || f.Drone.movement <= 0 || !NextRepair(f, mecha)) Return(f);
            }
            Publish(f);
        }
        Recount(factory);
    }
    private bool NextRepair(Flight f, Mecha mecha)
    {
        var factory = f.Factory; var best = 0f; var entity = 0; var generation = 0L;
        var stats = factory.constructionSystem.constructStats;
        for (var id = 1; id < stats.cursor; id++)
        {
            if (!ConstructionRepairTargets.Valid(factory, id)) continue;
            ref var stat = ref stats.buffer[id]; var target = stat.entityId;
            if ((factory.entityPool[target].pos - mecha.player.position).sqrMagnitude > mecha.buildArea * mecha.buildArea) continue;
            ref var combat = ref factory.skillSystem.combatStats.buffer[factory.entityPool[target].combatStatId];
            var value = factory.constructionSystem.GetRepairValue(ref stat, ref combat, (factory.entityPool[target].pos - (Vector3)f.Craft.pos).sqrMagnitude);
            if (value <= best || !Multiplayer.Session.AuthorityRuntime.FactoryCombatHost.TryGetEntityKey(factory, target, out var key)) continue;
            best = value; entity = target; generation = key.Generation;
        }
        if (entity == 0) return false;
        f.Entity = entity; f.Generation = generation; f.Drone.targetObjectId = entity;
        f.Drone.targetPos = factory.constructionSystem._obj_hpos(entity); f.Drone.stage = 2;
        Recount(factory); return true;
    }
    private void Return(Flight f)
    {
        if (f.Returning) return;
        f.Returning = true;
        if (!f.Started) { End(f); return; }
        f.Drone.stage = 4; f.Drone.targetObjectId = 0; f.Drone.movement = 0;
        Recount(f.Factory); Publish(f);
    }
    private void End(Flight f)
    {
        if (!flights.Remove(f.Id)) return;
        if (f.Native) nativeBindings.Remove((f.Factory, f.NativeId));
        f.Returning = true; Publish(f, true); Recount(f.Factory);
    }
    public void Recount(PlanetFactory factory)
    {
        var system = factory.constructionSystem; var counts = new Dictionary<int, int>(); var drones = system.drones;
        for (var id = 1; id < drones.cursor; id++)
        {
            ref var d = ref drones.buffer[id];
            if (d.id == id && d.stage > 0 && d.stage < 4 && d.targetObjectId > 0)
            { counts.TryGetValue(d.targetObjectId, out var count); counts[d.targetObjectId] = count + 1; }
        }
        foreach (var f in flights.Values)
            if (!f.Native && f.Factory == factory && !f.Returning)
            { counts.TryGetValue(f.Entity, out var count); counts[f.Entity] = count + 1; }
        for (var id = 1; id < system.constructStats.cursor; id++)
        {
            ref var stat = ref system.constructStats.buffer[id];
            if (stat.id != id) continue;
            counts.TryGetValue(stat.entityId, out var count); stat.repairerCount = count;
        }
    }
    private void Publish(Flight f, bool complete = false)
    {
        var packet = StatePacket(f, complete);
        if (packet == null) return;
        Multiplayer.Session.Network.SendPacketToStar(packet, f.Factory.planet.star.id);
        var player = Multiplayer.Session.Server.Players.Get(f.Player);
        if (player != null && player.Data.LocalStarId != f.Factory.planet.star.id) player.Connection.SendPacket(packet);
        if (!f.Native) Multiplayer.Session.Drones.ApplyRepairPose(packet);
    }
    private RepairDroneStatePacket StatePacket(Flight f, bool complete = false, bool snapshot = false)
    {
        if (f.Native && f.NativeId < f.Factory.constructionSystem.drones.cursor && f.Factory.constructionSystem.drones.buffer[f.NativeId].id == f.NativeId)
        {
            f.Drone = f.Factory.constructionSystem.drones.buffer[f.NativeId];
            f.Craft = f.Factory.craftPool[f.Drone.craftId];
        }
        var drone = f.Drone; var craft = f.Craft;
        var mecha = Multiplayer.Session.CombatAuthority.MechaFor(f.Player);
        var power = f.Native ? f.Factory.craftAnimPool[drone.craftId].power : f.Power;
        var eject = mecha?.player != null ? Eject(mecha.player.position) : (Vector3)craft.pos;
        var state = DroneFlightState.Capture(drone, craft, eject, f.Factory.gameData.history.constructionDroneSpeed,
            mecha?.coreEnergy > 0, power);
        if (complete) state.Stage = 0;
        if (!complete && !snapshot && f.Published.HasValue && f.PublishedGeneration == f.Generation && f.Published.Value.SameCommand(state)) return null;
        if (!snapshot) { f.Published = state; f.PublishedGeneration = f.Generation; }
        var packet = new RepairDroneStatePacket
        {
            Flight = f.Id,
            PlayerId = f.Player,
            PlanetId = f.Factory.planetId,
            Tick = GameMain.gameTick,
            Stage = complete ? 0 : drone.stage,
            X = (float)craft.pos.x,
            Y = (float)craft.pos.y,
            Z = (float)craft.pos.z,
            Qx = craft.rot.x,
            Qy = craft.rot.y,
            Qz = craft.rot.z,
            Qw = craft.rot.w,
            EntityId = f.Entity,
            Generation = f.Generation,
            FlightState = state.Encode(),
            Sequence = ++nextState
        };
        return packet;
    }
    public void SendSnapshot(PlanetFactory factory, NebulaeConnection connection)
    {
        foreach (var f in flights.Values)
            if (f.Factory == factory && f.Started) connection.SendPacket(StatePacket(f, snapshot: true));
    }
    public void ReceiveState(RepairDroneStatePacket packet)
    {
        if (packet.Flight <= 0 || packet.Stage < 0 || packet.Stage > 4 || !Finite(packet.X) || !Finite(packet.Y) || !Finite(packet.Z) ||
            !Finite(packet.Qx) || !Finite(packet.Qy) || !Finite(packet.Qz) || !Finite(packet.Qw) ||
            !DroneFlightState.TryDecode(packet.FlightState, out var state) || state.Stage != packet.Stage) return;
        if (packet.Stage == 0) RememberEnd(packet.Flight);
        else if (ended.Contains(packet.Flight)) return;
        if (packet.PlayerId != Multiplayer.Session.LocalPlayer.Id) { Multiplayer.Session.Drones.ApplyRepairPose(packet); return; }
        if (!bindings.TryGetValue(packet.Flight, out var b) || packet.Sequence <= b.Tick) return;
        b.Tick = packet.Sequence;
        var factory = b.Factory; var pool = factory.constructionSystem.drones;
        if (b.Drone >= pool.cursor || pool.buffer[b.Drone].id != b.Drone) { bindings.Remove(b.Id); nativeBindings.Remove((factory, b.Drone)); return; }
        ref var drone = ref pool.buffer[b.Drone];
        if (b.View == null) b.View = new DroneFlightView(state);
        else b.View.Apply(state);
        drone.stage = b.View.Drone.stage;
        drone.targetObjectId = b.View.Drone.targetObjectId;
    }
    private static bool Finite(float n) => !float.IsNaN(n) && !float.IsInfinity(n);
    public void Render(PlanetFactory factory, ObjectRenderer[] renderers, bool syncGpu, float dt)
    {
        if (Multiplayer.Session.IsServer) return;
        foreach (var b in bindings.Values.ToArray())
        {
            if (b.Factory != factory || b.Drone >= factory.constructionSystem.drones.cursor) continue;
            ref var drone = ref factory.constructionSystem.drones.buffer[b.Drone];
            if (drone.id != b.Drone || b.View == null) continue;
            var eject = Eject(GameMain.mainPlayer.position);
            b.View.Advance(factory, eject, dt);
            if (b.View.Finished)
            {
                bindings.Remove(b.Id); nativeBindings.Remove((factory, b.Drone));
                drone.targetObjectId = drone.nextTarget1ObjectId = drone.nextTarget2ObjectId = drone.nextTarget3ObjectId = 0;
                GameMain.mainPlayer.mecha.constructionModule.RecycleDrone(factory, ref drone);
                continue;
            }
            ref var craft = ref factory.craftPool[drone.craftId]; ref var animation = ref factory.craftAnimPool[drone.craftId];
            b.View.WritePose(ref craft, ref animation, eject, b.Drone);
            drone.stage = b.View.Drone.stage;
            if (syncGpu)
            {
                NebulaeWorld.Player.DroneManager.UpdateGpuInstance(renderers, in craft, drone.stage);
                factory.planet.factoryModel.craftDirty = true;
            }
        }
    }
    private void RememberEnd(long token)
    {
        if (!ended.Add(token)) return;
        endedOrder.Enqueue(token); if (endedOrder.Count > 4096) ended.Remove(endedOrder.Dequeue());
    }
    public void AbortPlanet(PlanetFactory factory)
    {
        foreach (var key in continuations.Keys.Where(k => factory == null || k.Factory == factory).ToArray()) continuations.Remove(key);
        foreach (var b in bindings.Values.Where(b => factory == null || b.Factory == factory).ToArray())
        {
            Cancel(b); bindings.Remove(b.Id); nativeBindings.Remove((b.Factory, b.Drone)); RememberEnd(b.Id);
            var pool = b.Factory.constructionSystem.drones;
            if (b.Drone >= pool.cursor || pool.buffer[b.Drone].id != b.Drone) continue;
            ref var d = ref pool.buffer[b.Drone]; d.targetObjectId = d.nextTarget1ObjectId = d.nextTarget2ObjectId = d.nextTarget3ObjectId = 0;
            GameMain.mainPlayer.mecha.constructionModule.RecycleDrone(b.Factory, ref d);
        }
    }
    public void PlayerLeft(ushort player)
    {
        foreach (var f in flights.Values.Where(f => f.Player == player).ToArray()) End(f);
        foreach (var key in sequences.Keys.Where(k => k.Player == player).ToArray()) { sequences.Remove(key); offers.Remove(key); }
    }
    public void PlayerDied(ushort player)
    {
        foreach (var key in offers.Keys.Where(k => k.Player == player).ToArray()) offers.Remove(key);
        if (Multiplayer.Session.IsServer)
        {
            foreach (var flight in flights.Values.Where(f => f.Player == player).ToArray()) End(flight);
        }
        else if (player == Multiplayer.Session.LocalPlayer.Id)
        {
            AbortPlanet(null);
            lastOffers.Clear();
        }
    }
}
