using System.Collections.Generic;
using System.IO;
using System.Linq;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Factory;
using NebulaeWorld.Player;

namespace NebulaeWorld.Factory;

internal sealed partial class BuildDispatchManager
{
    private sealed class BaseFlight
    {
        public long Generation, Flight;
        public int DroneId;
        public DroneFlightState State;
        public DroneFlightView View;
    }
    private sealed class BaseView
    {
        public PlanetFactory Factory;
        public long Sequence;
        public bool SnapshotRequested;
        public readonly Dictionary<(int Entity, int Slot), BaseFlight> Flights = new();
    }
    private readonly Dictionary<int, BaseView> baseViews = new();
    private readonly Dictionary<PlanetFactory, BaseView> publishedBaseViews = new();
    private long nextBaseFlight, nextBaseSequence;
    private const int BaseRecordSize = 24 + DroneFlightState.WireSize;

    private void ClearBaseViews() { baseViews.Clear(); publishedBaseViews.Clear(); }

    public void PublishBaseDrones(PlanetFactory factory, long tick)
    {
        if (!publishedBaseViews.TryGetValue(factory, out var published))
            publishedBaseViews[factory] = published = new BaseView { Factory = factory };
        var changed = CaptureBaseFlights(factory, published);
        if (changed.Count == 0) return;
        Multiplayer.Session.Network.SendPacketToStar(BasePacket(factory, tick, changed, false), factory.planet.star.id);
    }

    private Dictionary<(int Entity, int Slot), BaseFlight> CaptureBaseFlights(PlanetFactory factory, BaseView published)
    {
        var changed = new Dictionary<(int Entity, int Slot), BaseFlight>();
        var seen = new HashSet<(int Entity, int Slot)>();
        var slots = new Dictionary<int, int>();
        var system = factory.constructionSystem; var drones = system.drones;
        for (var id = 1; id < drones.cursor; id++)
        {
            ref var drone = ref drones.buffer[id];
            if (drone.id != id || drone.owner <= 0 || drone.owner >= system.constructionModules.cursor) continue;
            var module = system.constructionModules.buffer[drone.owner];
            if (module?.id != drone.owner || module.entityId <= 0 ||
                Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, module.entityId, out var key) != true) continue;
            slots.TryGetValue(module.entityId, out var slot); slots[module.entityId] = slot + 1;
            var identity = (module.entityId, slot); seen.Add(identity);
            ref var craft = ref factory.craftPool[drone.craftId]; ref var animation = ref factory.craftAnimPool[drone.craftId];
            var baseId = factory.entityPool[module.entityId].battleBaseId;
            var powered = baseId > 0 && factory.defenseSystem.battleBases.buffer[baseId]?.energy > 0;
            var state = DroneFlightState.Capture(drone, craft, module.baseEjectPos,
                factory.gameData.history.constructionDroneSpeed, powered, animation.power, animation.time);
            published.Flights.TryGetValue(identity, out var previous);
            var launch = previous == null || previous.Generation != key.Generation || previous.DroneId != id ||
                drone.stage == 1 && (previous.State.Stage == 0 || !previous.State.InitialVector.Equals(state.InitialVector));
            if (!launch && previous.State.SameCommand(state)) { previous.State = state; continue; }
            var flight = new BaseFlight { Generation = key.Generation, DroneId = id, Flight = launch ? ++nextBaseFlight : previous.Flight, State = state };
            published.Flights[identity] = flight; changed[identity] = flight;
        }
        foreach (var identity in published.Flights.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            var removed = published.Flights[identity];
            changed[identity] = new BaseFlight { Generation = removed.Generation, Flight = removed.Flight };
            published.Flights.Remove(identity);
        }
        return changed;
    }

    private BaseDroneStatePacket BasePacket(PlanetFactory factory, long tick, Dictionary<(int Entity, int Slot), BaseFlight> flights, bool full)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(flights.Count);
        foreach (var pair in flights)
        {
            writer.Write(pair.Key.Entity); writer.Write(pair.Value.Generation); writer.Write(pair.Key.Slot); writer.Write(pair.Value.Flight);
            pair.Value.State.Write(writer); // Speed == 0 is a removal tombstone.
        }
        return new BaseDroneStatePacket { PlanetId = factory.planetId, Tick = tick, Sequence = ++nextBaseSequence, Full = full, State = stream.ToArray() };
    }

    public void SendBaseDroneSnapshot(PlanetFactory factory, NebulaeConnection connection)
    {
        // Flush pending events to existing observers before answering the new observer.
        PublishBaseDrones(factory, GameMain.gameTick);
        connection.SendPacket(BasePacket(factory, GameMain.gameTick, publishedBaseViews[factory].Flights, true));
    }

    public void ReceiveBaseDrones(BaseDroneStatePacket packet)
    {
        var factory = GameMain.galaxy?.PlanetById(packet.PlanetId)?.factory;
        if (factory == null || packet.State == null || packet.State.Length < 4 || packet.State.Length > 4 + 65536 * BaseRecordSize) return;
        if (baseViews.TryGetValue(packet.PlanetId, out var view) && view.Factory == factory && view.Sequence >= packet.Sequence) return;
        using var stream = new MemoryStream(packet.State, false); using var reader = new BinaryReader(stream);
        var count = reader.ReadInt32();
        if (count < 0 || count > 65536 || stream.Length != 4L + count * BaseRecordSize) return;
        var incoming = new Dictionary<(int Entity, int Slot), BaseFlight>();
        for (var i = 0; i < count; i++)
        {
            var entity = reader.ReadInt32(); var generation = reader.ReadInt64(); var slot = reader.ReadInt32(); var flight = reader.ReadInt64();
            var state = DroneFlightState.Read(reader);
            if (entity <= 0 || generation <= 0 || slot < 0 || slot >= 256 || flight <= 0 ||
                state.Speed != 0 && !state.Valid || incoming.ContainsKey((entity, slot))) return;
            incoming.Add((entity, slot), new BaseFlight { Generation = generation, Flight = flight, State = state });
        }
        if (view == null || view.Factory != factory) baseViews[packet.PlanetId] = view = new BaseView { Factory = factory };
        if (packet.Full)
            foreach (var identity in view.Flights.Keys.Where(k => !incoming.ContainsKey(k)).ToArray()) view.Flights.Remove(identity);
        foreach (var pair in incoming)
        {
            if (pair.Value.State.Speed == 0) { view.Flights.Remove(pair.Key); continue; }
            if (view.Flights.TryGetValue(pair.Key, out var previous) && previous.Flight == pair.Value.Flight && previous.Generation == pair.Value.Generation)
            {
                pair.Value.View = previous.View;
                pair.Value.View?.Apply(pair.Value.State);
            }
            else if (previous?.Generation == pair.Value.Generation && previous.View != null)
                pair.Value.View = new DroneFlightView(pair.Value.State, previous.View);
            view.Flights[pair.Key] = pair.Value;
        }
        view.Sequence = packet.Sequence;
        if (packet.Full) view.SnapshotRequested = true;
    }

    public void RenderBaseDrones(PlanetFactory factory, ObjectRenderer[] renderers, bool syncGpu, float dt)
    {
        if (!baseViews.TryGetValue(factory.planetId, out var view) || view.Factory != factory)
        {
            baseViews[factory.planetId] = view = new BaseView { Factory = factory, Sequence = -1 };
        }
        if (!view.SnapshotRequested)
        {
            view.SnapshotRequested = true;
            Multiplayer.Session.Network.SendPacket(new DroneFlightSnapshotRequestPacket { PlanetId = factory.planetId });
        }
        var drones = factory.constructionSystem.drones; var slots = new Dictionary<int, int>(); var idle = new Dictionary<int, int>();
        for (var id = 1; id < drones.cursor; id++)
        {
            ref var drone = ref drones.buffer[id];
            if (drone.id != id || drone.owner <= 0 || drone.owner >= factory.constructionSystem.constructionModules.cursor) continue;
            var module = factory.constructionSystem.constructionModules.buffer[drone.owner];
            if (module?.id != drone.owner || module.entityId <= 0) continue;
            slots.TryGetValue(module.entityId, out var slot); slots[module.entityId] = slot + 1;
            if (!view.Flights.TryGetValue((module.entityId, slot), out var flight) ||
                Multiplayer.Session.AuthorityRuntime.WorldReplica?.TryFindKey(PoolKind.Entity, factory.planetId, module.entityId, out var key) != true || key.Generation != flight.Generation) continue;
            flight.View ??= new DroneFlightView(flight.State);
            flight.View.Advance(factory, module.baseEjectPos, dt);
            ref var craft = ref factory.craftPool[drone.craftId]; ref var animation = ref factory.craftAnimPool[drone.craftId];
            flight.View.WritePose(ref craft, ref animation, module.baseEjectPos, id);
            drone.stage = flight.State.Stage;
            drone.targetObjectId = flight.State.Target;
            if (!idle.ContainsKey(module.id)) idle[module.id] = 0;
            if (flight.State.Stage == 0) idle[module.id]++;
            if (!syncGpu) continue;
            DroneManager.UpdateGpuInstance(renderers, in craft, flight.View.Drone.stage);
            factory.planet.factoryModel.craftDirty = true;
        }
        foreach (var pair in idle) factory.constructionSystem.constructionModules.buffer[pair.Key].droneIdleCount = pair.Value;
    }
}
