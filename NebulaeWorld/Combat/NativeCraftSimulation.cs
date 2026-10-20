using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using UnityEngine;

namespace NebulaeWorld.Combat;

internal static class NativeCraftSimulation
{
    private sealed class Entry
    {
        public int HostId, LocalId, Component;
        public CraftState State;
        public bool Pending = true;
        public VectorLF3 Position;
        public Quaternion Rotation;
    }
    private sealed class WorldState
    {
        public CraftData[] Pool;
        public readonly Dictionary<int, Entry> Crafts = new();
        public readonly Dictionary<int, List<Entry>> Fighters = new();
    }
    private static readonly ConditionalWeakTable<object, WorldState> worlds = new();

    public static void Install(object world, CraftData[] pool, int localId, int hostId, in CraftState state)
    {
        var registry = worlds.GetValue(world, _ => new WorldState());
        if (!ReferenceEquals(registry.Pool, pool)) { registry.Crafts.Clear(); registry.Fighters.Clear(); registry.Pool = pool; }
        Remove(world, localId);
        var component = CraftNativeStateCodec.TryFleet(state.NativeFleet, out var fleet) ? fleet.id :
            CraftNativeStateCodec.TryUnit(state.NativeUnit, out var unit) ? unit.id : 0;
        var entry = new Entry
        {
            HostId = hostId,
            LocalId = localId,
            Component = component,
            State = state,
            Position = pool[localId].pos,
            Rotation = pool[localId].rot
        };
        registry.Crafts[localId] = entry;
        if (state.NativeUnit?.Length > 0 && state.Owner > 0)
        {
            if (!registry.Fighters.TryGetValue(state.Owner, out var children)) registry.Fighters[state.Owner] = children = new List<Entry>();
            children.Add(entry);
        }
    }
    public static void Remove(object world, int localId)
    {
        if (world == null || !worlds.TryGetValue(world, out var registry) || !registry.Crafts.TryGetValue(localId, out var entry)) return;
        registry.Crafts.Remove(localId);
        if (registry.Fighters.TryGetValue(entry.State.Owner, out var children))
        { children.Remove(entry); if (children.Count == 0) registry.Fighters.Remove(entry.State.Owner); }
    }

    private static bool ScopeReady(int planet) => planet > 0
        ? NativeEnemySimulation.IsLive(PoolKind.GroundCraft, planet) && NativeEnemySimulation.IsLive(PoolKind.GroundEnemy, planet) &&
          NativeEnemySimulation.IsLive(PoolKind.Entity, planet)
        : NativeEnemySimulation.IsLive(PoolKind.SpaceCraft, 0) && NativeEnemySimulation.IsLive(PoolKind.SpaceEnemy, 0);
    public static bool CanPresent(int astro, int hostId)
    {
        if (astro <= 0 || hostId <= 0) return false;
        var planet = SpaceSector.IsPlanetAstro(astro) ? astro : 0;
        return (planet == 0 || GameMain.localPlanet?.id == planet) && LocalCraftId(planet, hostId, out var localId) &&
            World(planet) is { } world && ScopeReady(planet) && worlds.TryGetValue(world, out var registry) &&
            registry.Crafts.TryGetValue(localId, out var entry) && registry.Pool[localId].id == localId && entry.Component > 0 &&
            (registry.Pool[localId].unitId == entry.Component || registry.Pool[localId].fleetId == entry.Component) &&
            (entry.State.Owner != -1 && entry.State.PlayerOwner == 0 || PlayerMecha(entry.State.PlayerOwner) != null);
    }
    public static int HostId(int astro, int localId)
    {
        var world = World(SpaceSector.IsPlanetAstro(astro) ? astro : 0);
        return world != null && worlds.TryGetValue(world, out var registry) && registry.Crafts.TryGetValue(localId, out var entry) ? entry.HostId : localId;
    }
    public static long AppliedTick(object world, int localId) => world != null && worlds.TryGetValue(world, out var registry) &&
        registry.Crafts.TryGetValue(localId, out var entry) && !entry.Pending ? entry.State.SampleTick : -1;

    public static bool CanRun(object world, int localId)
    {
        if (!worlds.TryGetValue(world, out var registry) || !registry.Crafts.TryGetValue(localId, out var entry) ||
            entry.Pending || GameMain.gameTick <= entry.State.SampleTick || registry.Pool[localId].id != localId) return false;
        var planet = world is PlanetFactory factory ? factory.planetId : 0;
        if (!ScopeReady(planet)) return false;
        // Remote craft checkpoints may name enemies intentionally absent from this client's pool.
        // Keep their presentation, but only run native targeting in the subscribed star.
        if (planet == 0 && (GameMain.localStar == null ||
            entry.State.AstroId / 100 != GameMain.localStar.id)) return false;
        return entry.State.Owner != -1 && entry.State.PlayerOwner == 0 || PlayerMecha(entry.State.PlayerOwner) != null;
    }
    public static int FleetId(ref FleetComponent fleet)
    {
        if (!WorldSimulation.IsClient) return fleet.id;
        var world = World(FleetPlanet);
        return world != null && CanRun(world, fleet.craftId) &&
            worlds.TryGetValue(world, out var registry) && registry.Crafts[fleet.craftId].Component == fleet.id ? fleet.id : 0;
    }
    public static int UnitId(ref UnitComponent unit)
    {
        if (!WorldSimulation.IsClient) return unit.id;
        var world = World(FleetPlanet);
        return world != null && CanRun(world, unit.craftId) &&
            worlds.TryGetValue(world, out var registry) && registry.Crafts[unit.craftId].Component == unit.id ? unit.id : 0;
    }
    [ThreadStatic] public static int FleetPlanet;
    private static object World(int planet) => planet > 0 ? GameMain.galaxy?.PlanetById(planet)?.factory : GameMain.spaceSector;

    public static Mecha OwnerMecha(bool space, int planet, int localId)
    {
        var world = World(space ? 0 : planet);
        return world != null && worlds.TryGetValue(world, out var registry) && registry.Crafts.TryGetValue(localId, out var entry)
            ? PlayerMecha(entry.State.PlayerOwner) : null;
    }
    private static Mecha PlayerMecha(ushort id)
    {
        var session = Multiplayer.Session;
        if (session == null || id == 0) return null;
        if (id == session.LocalPlayer.Id) return GameMain.mainPlayer?.mecha;
        var combat = session.Combat;
        return combat?.IndexByPlayerId != null && combat.IndexByPlayerId.TryGetValue(id, out var index) ? combat.Players[index].mecha : null;
    }
    public static void Prepare(PlanetFactory factory)
    {
        if (factory?.planet.factoryLoaded == true)
            Prepare(factory, factory.craftPool, factory.combatGroundSystem.fleets, factory.combatGroundSystem.units, factory.planetId);
    }
    public static void Prepare(SpaceSector sector)
    {
        if (sector?.combatSpaceSystem != null)
            Prepare(sector, sector.craftPool, sector.combatSpaceSystem.fleets, sector.combatSpaceSystem.units, 0);
    }
    private static void Prepare(object world, CraftData[] pool, DataPool<FleetComponent> fleets, DataPool<UnitComponent> units, int planet)
    {
        if (!ScopeReady(planet) || !worlds.TryGetValue(world, out var registry) || !ReferenceEquals(pool, registry.Pool)) return;
        foreach (var entry in registry.Crafts.Values)
        {
            ref var craft = ref pool[entry.LocalId];
            if (craft.id != entry.LocalId) continue;
            if (entry.Pending)
            {
                var owner = entry.State.Owner;
                if (entry.State.NativeUnit?.Length > 0 && owner > 0)
                {
                    if (!LocalCraftId(planet, owner, out owner)) continue;
                    if (pool[owner].fleetId <= 0) continue;
                }
                craft.owner = owner;
                if (CraftNativeStateCodec.TryFleet(entry.State.NativeFleet, out var fleet))
                {
                    if (!Reserve(fleets, fleet.id, value => value.id == 0 || value.craftId == entry.LocalId)) continue;
                    fleet.craftId = entry.LocalId; fleets.buffer[fleet.id] = fleet; craft.fleetId = fleet.id;
                }
                else if (CraftNativeStateCodec.TryUnit(entry.State.NativeUnit, out var unit))
                {
                    if (!Reserve(units, unit.id, value => value.id == 0 || value.craftId == entry.LocalId)) continue;
                    unit.craftId = entry.LocalId; units.buffer[unit.id] = unit; craft.unitId = unit.id;
                }
                else { craft.fleetId = craft.unitId = 0; }
                entry.Pending = false;
            }
            var mecha = PlayerMecha(entry.State.PlayerOwner);
            if (craft.fleetId > 0 && mecha != null)
            {
                var module = planet > 0 ? mecha.groundCombatModule : mecha.spaceCombatModule;
                if (craft.port >= 0 && craft.port < module.fleetCount)
                {
                    ref var fleet = ref module.moduleFleets[craft.port];
                    fleet.fleetId = craft.fleetId; fleet.fleetAstroId = planet > 0 ? planet : craft.astroId;
                    if (registry.Fighters.TryGetValue(entry.HostId, out var children))
                        foreach (var child in children)
                            if (child.State.Port >= 0 && child.State.Port < fleet.fighters.Length)
                                fleet.fighters[child.State.Port].craftId = child.LocalId;
                }
            }
            if (world is PlanetFactory ground)
                CraftPresentation.RefreshGround(ground, entry.LocalId, craft.pos != entry.Position, craft.rot != entry.Rotation);
            entry.Position = craft.pos; entry.Rotation = craft.rot;
        }
    }
    private static bool LocalCraftId(int planet, int hostId, out int localId)
    {
        localId = 0;
        var replica = Multiplayer.Session?.AuthorityRuntime?.WorldReplica;
        if (replica == null || !replica.TryFindKey(planet > 0 ? PoolKind.GroundCraft : PoolKind.SpaceCraft, planet, hostId, out var key) ||
            !replica.Mirrors.TryGetValue(key, out var mirror)) return false;
        localId = mirror.LocalComponentId; return localId > 0;
    }
    private static bool Reserve<T>(DataPool<T> pool, int id, Func<T, bool> available) where T : struct, IPoolElement
    {
        if (id <= 0 || id > CraftNativeStateCodec.MaxPoolId) return false;
        if (pool.capacity <= id) pool.SetCapacity(Math.Max(id + 1, pool.capacity * 2));
        if (!available(pool.buffer[id])) return false;
        if (id >= pool.cursor)
        {
            for (var hole = pool.cursor; hole < id; hole++) pool.recycleIds[pool.recycleCursor++] = hole;
            pool.cursor = id + 1;
        }
        else for (var slot = 0; slot < pool.recycleCursor; slot++)
                if (pool.recycleIds[slot] == id) { pool.recycleIds[slot] = pool.recycleIds[--pool.recycleCursor]; break; }
        return true;
    }
}
