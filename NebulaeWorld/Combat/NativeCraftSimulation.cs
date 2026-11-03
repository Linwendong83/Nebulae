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
        public readonly CraftMotionHistory Motion = new();
    }
    private sealed class WorldState
    {
        public CraftData[] Pool;
        public readonly Dictionary<int, Entry> Crafts = new();
        public readonly Dictionary<int, List<Entry>> Fighters = new();
        public long NextBaseFleetSweep;
    }
    private static readonly ConditionalWeakTable<object, WorldState> worlds = new();
    // Runtime probe switch: "0" reproduces the pre-fix client, where a battle-base fleet never got
    // the module foreign key the native owner check requires. Default keeps the link.
    private static readonly bool LinkBaseFleet = Environment.GetEnvironmentVariable("NEBULAE_BASE_FLEET_LINK") != "0";
    // How often the backstop sweep re-checks base module keys; the removal path clears them directly.
    private const long BaseFleetSweepTicks = 30;

    public static void Install(object world, CraftData[] pool, int localId, int hostId, in CraftState state)
    {
        var registry = worlds.GetValue(world, _ => new WorldState());
        if (!ReferenceEquals(registry.Pool, pool)) { registry.Crafts.Clear(); registry.Fighters.Clear(); registry.Pool = pool; }
        var component = CraftNativeStateCodec.TryFleet(state.NativeFleet, out var fleet) ? fleet.id :
            CraftNativeStateCodec.TryUnit(state.NativeUnit, out var unit) ? unit.id : 0;
        if (registry.Crafts.TryGetValue(localId, out var existing) && existing.HostId == hostId &&
            existing.Component == component && SameFrame(existing.State, state))
        {
            existing.State = state; existing.Pending = true;
            return;
        }
        Remove(world, localId);
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

    private static bool SameFrame(in CraftState a, in CraftState b) => a.AstroId == b.AstroId &&
        a.Owner == b.Owner && a.PlayerOwner == b.PlayerOwner && a.ProtoId == b.ProtoId && a.IsDynamic && b.IsDynamic;

    public static void CaptureMotion(object world, long tick)
    {
        if (world == null || !worlds.TryGetValue(world, out var registry)) return;
        foreach (var entry in registry.Crafts.Values)
            if (!entry.Pending && registry.Pool[entry.LocalId].id == entry.LocalId)
                entry.Motion.Record(tick, registry.Pool[entry.LocalId].pos);
    }

    public static VectorLF3 ReconcilePosition(object world, int id, in CraftState state, VectorLF3 current)
    {
        var position = new VectorLF3(state.PosX, state.PosY, state.PosZ);
        if (!worlds.TryGetValue(world, out var registry) || !registry.Crafts.TryGetValue(id, out var entry)) return position;
        if (SameFrame(entry.State, state) && entry.Motion.Reconcile(state.SampleTick, position, current, state.IsSpace ? 5000 : 50))
            return current;
        entry.Motion.Reset();
        return position;
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
            PredictsWeapons(entry.State);
    }
    private static bool PredictsWeapons(in CraftState state) => state.PlayerOwner == 0
        ? state.Owner != -1 : state.PlayerOwner == Multiplayer.Session?.LocalPlayer?.Id;
    public static bool PredictsWeapons(int planet, int localId) => World(planet) is { } world &&
        worlds.TryGetValue(world, out var registry) && registry.Crafts.TryGetValue(localId, out var entry) && PredictsWeapons(entry.State);
    public static bool IsBase(int astro, int hostId)
    {
        var planet = SpaceSector.IsPlanetAstro(astro) ? astro : 0;
        return LocalCraftId(planet, hostId, out var id) && World(planet) is { } world &&
            worlds.TryGetValue(world, out var registry) && registry.Crafts.TryGetValue(id, out var entry) &&
            entry.State.PlayerOwner == 0 && entry.State.Owner != -1;
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
        if (!MatchesModule(registry, entry, planet)) return false;
        // The native engage/repair loops read "defenseSystem.battleBases.buffer[id].energy"
        // without a null check. A replicated fleet (or its units) can arrive before, or outlive,
        // the base component on this client; running it then dereferences a freed pool slot.
        if (!OwnerBasePresent(world, registry.Pool, localId)) return false;
        // Native formation code averages unlocked ports. Craft facts can arrive before
        // the matching research/module data; a zero count would produce NaN positions.
        if (entry.State.NativeUnit?.Length > 0 &&
            (planet > 0 ? GameMain.history.groundFleetPortCount : GameMain.history.spaceFleetPortCount) <= registry.Pool[localId].port)
            return false;
        // Remote craft checkpoints may name enemies intentionally absent from this client's pool.
        // Keep their presentation, but only run native targeting in the subscribed star.
        if (planet == 0 && (GameMain.localStar == null ||
            entry.State.AstroId / 100 != GameMain.localStar.id)) return false;
        return entry.State.Owner != -1 && entry.State.PlayerOwner == 0 || PlayerMecha(entry.State.PlayerOwner) != null;
    }
    // Duplicates the native owner walk: a fleet names the battle-base entity, a unit names its
    // fleet craft. Anything still pointing at a base that no longer exists locally is not run.
    private static bool OwnerBasePresent(object world, CraftData[] pool, int localId)
    {
        if (world is not PlanetFactory factory) return true;
        var craft = pool[localId];
        if (craft.unitId <= 0 && craft.fleetId <= 0) return true; // not a fleet or fighter craft
        var owner = craft.owner;
        if (craft.unitId > 0)
        {
            if (owner <= 0 || owner >= pool.Length || pool[owner].id != owner) return false;
            owner = pool[owner].owner;
        }
        if (owner <= 0) return true;
        var entity = factory.entityPool;
        if (entity == null) return true;
        if (owner >= entity.Length || owner >= factory.entityCursor || entity[owner].id != owner) return false;
        var id = entity[owner].battleBaseId;
        var bases = factory.defenseSystem?.battleBases;
        return id > 0 && bases != null && id < bases.cursor && bases.buffer[id]?.id == id;
    }
    private static bool MatchesModule(WorldState registry, Entry entry, int planet)
    {
        if (entry.State.PlayerOwner == 0) return true;
        var mecha = PlayerMecha(entry.State.PlayerOwner);
        var module = planet > 0 ? mecha?.groundCombatModule : mecha?.spaceCombatModule;
        var parent = entry;
        if (entry.State.NativeUnit?.Length > 0)
        {
            if (!LocalCraftId(planet, entry.State.Owner, out var owner) || !registry.Crafts.TryGetValue(owner, out parent)) return false;
        }
        var index = parent.State.Port;
        if (module?.moduleFleets == null || index < 0 || index >= module.fleetCount || index >= module.moduleFleets.Length) return false;
        ref var fleet = ref module.moduleFleets[index];
        if (fleet.protoId != parent.State.ProtoId || fleet.fighters == null) return false;
        return ReferenceEquals(parent, entry) || entry.State.Port >= 0 && entry.State.Port < fleet.fighters.Length &&
            fleet.fighters[entry.State.Port].itemId == entry.State.ProtoId;
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
            craft.pos += entry.Motion.Advance(GameMain.gameTick);
            LinkOwnerModule(world, registry, entry, ref craft, planet, PlayerMecha(entry.State.PlayerOwner));
            if (world is PlanetFactory ground)
                CraftPresentation.RefreshGround(ground, entry.LocalId, craft.pos != entry.Position, craft.rot != entry.Rotation);
            else CraftPresentation.RefreshSpace((SpaceSector)world, entry.LocalId);
            entry.Position = craft.pos; entry.Rotation = craft.rot;
        }
        if (LinkBaseFleet && planet > 0 && world is PlanetFactory baseFactory) ReconcileBaseFleets(baseFactory, registry);
    }
    /// <summary>
    /// Drops the battle-base module key that named a fleet replica which is being released.
    /// </summary>
    /// <remarks>
    /// The host clears the key in <c>ModuleFleet.OnFleetComponentRemoved</c> when it removes the fleet
    /// component; a client's fleet disappears through the craft binding instead, which never touches
    /// the module. Called with the craft still live, so the owner walk in
    /// <see cref="LinkOwnerModule"/> can still resolve the module.
    /// </remarks>
    internal static void UnlinkBaseFleet(PlanetFactory factory, int craftId)
    {
        if (!LinkBaseFleet || factory?.combatGroundSystem == null) return;
        var pool = factory.craftPool;
        if (craftId <= 0 || craftId >= factory.craftCursor || craftId >= pool.Length) return;
        ref var craft = ref pool[craftId];
        if (craft.id != craftId || craft.fleetId <= 0 || craft.unitId > 0 || craft.port < 0) return;
        var entity = factory.entityPool;
        var owner = craft.owner;
        if (entity == null || owner <= 0 || owner >= entity.Length || owner >= factory.entityCursor ||
            entity[owner].id != owner) return;
        var modules = factory.combatGroundSystem.combatModules;
        var moduleId = entity[owner].combatModuleId;
        if (modules?.buffer == null || moduleId <= 0 || moduleId >= modules.cursor || moduleId >= modules.buffer.Length) return;
        var module = modules.buffer[moduleId];
        if (module?.id != moduleId || module.moduleFleets == null ||
            craft.port >= module.fleetCount || craft.port >= module.moduleFleets.Length) return;
        ref var slot = ref module.moduleFleets[craft.port];
        if (slot.fleetId != craft.fleetId) return;
        slot.OnFleetComponentRemoved();
    }

    /// <summary>
    /// Clears a battle-base module key that no longer names a live fleet component.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The base panel and the fighter-port entries read <c>moduleFleets[0].fleetId &gt; 0</c> to decide
    /// whether a sortie is out: the panel reports 出击/召回 and locks the fighter-type button, and a
    /// fighter cannot be taken out of the hangar while the key is set. A key left behind by a fleet
    /// that already landed therefore keeps describing a sortie that is not happening.
    /// </para>
    /// <para>
    /// <see cref="UnlinkBaseFleet"/> covers the ordinary removal path. This sweep is the backstop for
    /// the ones that never reach it — a baseline that replaces the pool, a fleet removed while its
    /// craft record was not bound, or a key imported from the host's factory data for a fleet that no
    /// longer exists. It only ever drops a key that names nothing, and
    /// <see cref="LinkOwnerModule"/> restores it from the next replicated state.
    /// </para>
    /// </remarks>
    private static void ReconcileBaseFleets(PlanetFactory factory, WorldState registry)
    {
        var tick = GameMain.gameTick;
        if (tick < registry.NextBaseFleetSweep) return;
        registry.NextBaseFleetSweep = tick + BaseFleetSweepTicks;
        var modules = factory.combatGroundSystem?.combatModules;
        if (modules?.buffer == null) return;
        for (var id = 1; id < modules.cursor && id < modules.buffer.Length; id++)
        {
            var module = modules.buffer[id];
            // Only a battle base owns a module in the factory pool; a pilot's module is standalone.
            if (module?.id != id || module.entityId <= 0 || module.moduleFleets == null) continue;
            for (var index = 0; index < module.moduleFleets.Length; index++)
            {
                ref var slot = ref module.moduleFleets[index];
                if (slot.fleetId <= 0 || FleetAlive(factory, slot.fleetId)) continue;
                slot.OnFleetComponentRemoved();
            }
        }
    }

    // The same test FleetComponent.CheckOwnerExist applies: the pool slot is live and its craft is.
    private static bool FleetAlive(PlanetFactory factory, int fleetId)
    {
        var fleets = factory.combatGroundSystem?.fleets;
        if (fleets?.buffer == null || fleetId <= 0 || fleetId >= fleets.cursor || fleetId >= fleets.buffer.Length) return false;
        ref var fleet = ref fleets.buffer[fleetId];
        if (fleet.id != fleetId || fleet.craftId <= 0 || fleet.craftId >= factory.craftCursor ||
            fleet.craftId >= factory.craftPool.Length) return false;
        return factory.craftPool[fleet.craftId].id == fleet.craftId;
    }

    /// <summary>
    /// Restores the module foreign key native code uses to find a fleet's owner.
    /// </summary>
    /// <remarks>
    /// The native fleet loop calls <c>FleetComponent.CheckOwnerExist</c> before it steers anything,
    /// and that check requires <c>combatModules[entity].moduleFleets[port].fleetId</c> to name the
    /// fleet component. Vanilla only ever maintains that key for the pilot's own module, so a
    /// battle-base fleet replicated to a client has none: the check fails, the fleet craft is never
    /// steered, and its pose only moves on the sparse authoritative checkpoints while its drones
    /// chase a formation around the stale anchor. A base's module is derived from the owner entity,
    /// which the client already has, so the key can be rebuilt here each frame.
    /// </remarks>
    private static void LinkOwnerModule(object world, WorldState registry, Entry entry, ref CraftData craft, int planet, Mecha mecha)
    {
        if (craft.fleetId <= 0 || craft.port < 0) return;
        if (entry.State.PlayerOwner != 0)
        {
            if (mecha == null || entry.State.PlayerOwner != Multiplayer.Session?.LocalPlayer?.Id ||
                !MatchesModule(registry, entry, planet)) return;
            var module = planet > 0 ? mecha.groundCombatModule : mecha.spaceCombatModule;
            if (module == null || craft.port >= module.fleetCount) return;
            ref var fleet = ref module.moduleFleets[craft.port];
            fleet.fleetId = craft.fleetId; fleet.fleetAstroId = planet > 0 ? planet : craft.astroId;
            if (registry.Fighters.TryGetValue(entry.HostId, out var children))
                foreach (var child in children)
                    if (child.State.Port >= 0 && child.State.Port < fleet.fighters.Length)
                        fleet.fighters[child.State.Port].craftId = child.LocalId;
            return;
        }
        // A base fleet names its battle-base entity, and that entity names the combat module that
        // owns the port. Client combat modules are never replicated, so only this key is missing.
        if (!LinkBaseFleet || planet <= 0 || world is not PlanetFactory factory || craft.owner <= 0 || craft.unitId > 0) return;
        var entity = factory.entityPool;
        var owner = craft.owner;
        if (entity == null || owner >= entity.Length || owner >= factory.entityCursor || entity[owner].id != owner) return;
        var modules = factory.combatGroundSystem?.combatModules;
        var moduleId = entity[owner].combatModuleId;
        if (modules?.buffer == null || moduleId <= 0 || moduleId >= modules.cursor) return;
        var baseModule = modules.buffer[moduleId];
        if (baseModule == null || baseModule.id != moduleId || craft.port >= baseModule.fleetCount ||
            craft.port >= baseModule.moduleFleets.Length) return;
        ref var slot = ref baseModule.moduleFleets[craft.port];
        slot.fleetAstroId = planet;
        slot.fleetId = craft.fleetId;
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
