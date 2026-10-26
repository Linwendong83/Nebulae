#region

using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Combat;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>
/// The client-local pool mechanics behind <see cref="CraftBinding"/> for ground craft.
/// </summary>
/// <remarks>
/// <para>
/// Every method here touches vanilla pools; none decides whether a write should happen — that is the
/// binding core's call. Fleet/unit/drone/vehicle logic ids stay zero, preventing local shared
/// combat commits. CraftSimulation invokes native steering separately. Rendering,
/// colliders, audio and the spatial hash are built through the same display path the vanilla
/// creation uses, which is why pose/structure shows without running any rule.
/// </para>
/// <para>
/// Removal is always the pure path (<c>PlanetFactory.RemoveCraftWithComponents</c>), never a
/// destruction rule: despawn must not generate drops, statistics or ammo refunds. The host owns the
/// single destruction transaction; the client only removes the mirror.
/// </para>
/// </remarks>
internal sealed class GroundCraftReplicaPools : ILocalCraftPools
{
    private readonly int planetId;
    private readonly System.Collections.Generic.Dictionary<int, int> localSlots = [];

    public GroundCraftReplicaPools(int planetId)
    {
        this.planetId = planetId;
    }

    public bool TryGetLocalId(int hostId, out int localId) => localSlots.TryGetValue(hostId, out localId) && CraftExists(hostId);

    public bool CraftExists(int craftId)
    {
        var factory = Factory;
        if (!localSlots.TryGetValue(craftId, out craftId)) return false;
        return factory != null && craftId > 0 && craftId < factory.craftCursor &&
            craftId < factory.craftPool.Length && factory.craftPool[craftId].id == craftId;
    }

    public void CreateCraftShell(int craftId, in CraftState state)
    {
        var factory = Factory;
        if (factory == null || craftId <= 0) return;
        var hostId = craftId;
        if (localSlots.ContainsKey(hostId)) RemoveCraftShell(hostId);
        EnsureCapacity(factory, craftId);
        if (factory.craftPool[craftId].id == craftId &&
            (factory.craftPool[craftId].prototype == ECraftProto.ConstructionDrone ||
             localSlots.ContainsValue(craftId)))
        {
            // Client drone allocations may occupy a host combat craft's slot. Allocate a local
            // shell instead of removing the drone; the model still addresses it by host identity.
            var empty = default(CraftData);
            craftId = factory.AddCraftData(ref empty);
        }
        localSlots[hostId] = craftId;
        if (factory.craftPool[craftId].id == craftId)
        {
            factory.RemoveCraftWithComponents(craftId);
        }
        RemoveFromRecycle(factory, craftId);
        if (craftId >= factory.craftCursor)
        {
            factory.craftCursor = craftId + 1;
        }
        ref var craft = ref factory.craftPool[craftId];
        craft.SetEmpty();
        craft.id = craftId;
        craft.protoId = state.ProtoId;
        craft.modelIndex = state.ModelIndex;
        craft.astroId = planetId;
        craft.owner = state.Owner;
        craft.port = state.Port;
        craft.prototype = (ECraftProto)state.Prototype;
        craft.dynamic = state.IsDynamic;
        craft.isSpace = false;
        craft.stateFlags = state.StateFlags;
        craft.pos.x = state.PosX;
        craft.pos.y = state.PosY;
        craft.pos.z = state.PosZ;
        craft.rot.x = state.RotX;
        craft.rot.y = state.RotY;
        craft.rot.z = state.RotZ;
        craft.rot.w = state.RotW;
        craft.vel.x = state.VelX;
        craft.vel.y = state.VelY;
        craft.vel.z = state.VelZ;
        craft.combatStatId = 0;
        craft.droneId = 0;
        craft.fleetId = 0;
        craft.unitId = 0;
        craft.cAnchorId = 0;
        craft.vehicleId = 0;
        craft.vPartId = 0;
        craft.vdCockpitId = 0;
        craft.vdGyroscopeId = 0;
        craft.vdTyreId = 0;
        craft.vdSuspensionId = 0;
        craft.vdEngineId = 0;
        craft.vdWarpId = 0;
        craft.vdBatteryId = 0;
        craft.vdStorageId = 0;
        craft.vdFuelStorageId = 0;
        craft.vdTankId = 0;
        craft.vdConnectorId = 0;
        craft.vwGaussId = 0;
        craft.vwLaserId = 0;
        craft.vwCannonId = 0;
        craft.vwMissileId = 0;
        craft.vwPlasmaId = 0;
        craft.vwDisturbId = 0;
        craft.vwThrowId = 0;
        craft.vwShieldId = 0;
        craft.rigidId = 0;
        craft.ccrId = 0;
        craft.modelId = 0;
        craft.mmblockId = 0;
        craft.colliderId = 0;
        craft.audioId = 0;
        if (craft.dynamic)
        {
            craft.hashAddress = factory.hashSystemDynamic.AddObjectToBucket(craftId, craft.pos, EObjectType.Craft);
        }
        else
        {
            craft.hashAddress = factory.hashSystemStatic.AddObjectToBucket(craftId, craft.pos, EObjectType.Craft);
        }
        try
        {
            factory.CreateCraftDisplayComponents(craftId);
        }
        catch (System.Exception e)
        {
            NebulaeModel.Logger.Log.Warn("[authority] ground craft display creation failed for " + craftId + ": " + e.Message);
        }
        WriteCombatStat(factory, craftId, in state);
        NativeCraftSimulation.Install(factory, factory.craftPool, craftId, hostId, state);
        CraftPresentation.RefreshGround(factory, craftId, false, false);
    }

    public void WriteCraftState(int craftId, in CraftState state)
    {
        var factory = Factory;
        var hostId = craftId;
        if (!localSlots.TryGetValue(craftId, out craftId)) return;
        if (factory == null || craftId <= 0 || craftId >= factory.craftPool.Length) return;
        ref var craft = ref factory.craftPool[craftId];
        if (craft.id != craftId) return;
        craft.protoId = state.ProtoId;
        craft.modelIndex = state.ModelIndex;
        craft.owner = state.Owner;
        craft.port = state.Port;
        craft.stateFlags = state.StateFlags;
        var moved = craft.pos.x != state.PosX || craft.pos.y != state.PosY || craft.pos.z != state.PosZ;
        var rotated = craft.rot.x != state.RotX || craft.rot.y != state.RotY || craft.rot.z != state.RotZ || craft.rot.w != state.RotW;
        craft.pos = NativeCraftSimulation.ReconcilePosition(factory, craftId, state, craft.pos);
        craft.rot.x = state.RotX;
        craft.rot.y = state.RotY;
        craft.rot.z = state.RotZ;
        craft.rot.w = state.RotW;
        craft.vel.x = state.VelX;
        craft.vel.y = state.VelY;
        craft.vel.z = state.VelZ;
        WriteCombatStat(factory, craftId, in state);
        NativeCraftSimulation.Install(factory, factory.craftPool, craftId, hostId, state);
        CraftPresentation.RefreshGround(factory, craftId, moved, rotated);
    }

    public void RemoveCraftShell(int craftId)
    {
        var factory = Factory;
        if (!localSlots.TryGetValue(craftId, out var localId)) return;
        localSlots.Remove(craftId);
        craftId = localId;
        if (factory == null || craftId <= 0 || craftId >= factory.craftPool.Length) return;
        if (factory.craftPool[craftId].id != craftId) return;
        NativeCraftSimulation.Remove(factory, craftId);
        factory.RemoveCraftWithComponents(craftId);
    }

    private void WriteCombatStat(PlanetFactory factory, int craftId, in CraftState state)
    {
        ref var craft = ref factory.craftPool[craftId];
        var skill = GameMain.data.spaceSector.skillSystem;
        if (state.HasCombatStat)
        {
            int statId = craft.combatStatId;
            if (statId <= 0 || statId >= skill.combatStats.cursor || skill.combatStats.buffer[statId].id != statId ||
                skill.combatStats.buffer[statId].objectType != (int)EObjectType.Craft ||
                skill.combatStats.buffer[statId].objectId != craftId || skill.combatStats.buffer[statId].astroId != planetId)
            {
                statId = skill.combatStats.Add().id;
                craft.combatStatId = statId;
            }
            ref var stat = ref skill.combatStats.buffer[statId];
            stat.hp = state.Hp;
            stat.hpMax = state.HpMax;
            stat.hpRecover = state.HpRecover;
            stat.hpIncoming = state.HpIncoming;
            stat.astroId = stat.originAstroId = planetId;
            stat.objectType = (int)EObjectType.Craft;
            stat.objectId = craftId;
            stat.localPos = factory.craftPool[craftId].pos;
            stat.size = 1f;
        }
        else if (craft.combatStatId != 0)
        {
            var statId = craft.combatStatId;
            if (statId > 0 && statId < skill.combatStats.cursor && skill.combatStats.buffer[statId].id == statId &&
                skill.combatStats.buffer[statId].objectType == (int)EObjectType.Craft &&
                skill.combatStats.buffer[statId].objectId == craftId && skill.combatStats.buffer[statId].astroId == planetId)
            {
                skill.OnRemovingSkillTarget(statId, skill.combatStats.buffer[statId].originAstroId, ETargetType.CombatStat);
                skill.combatStats.Remove(statId);
            }
            craft.combatStatId = 0;
        }
    }

    private static void EnsureCapacity(PlanetFactory factory, int craftId)
    {
        while (craftId >= factory.craftCapacity)
        {
            factory.SetCraftCapacity(factory.craftCapacity * 2);
        }
    }

    private static void RemoveFromRecycle(PlanetFactory factory, int craftId)
    {
        for (var i = 0; i < factory.craftRecycleCursor; i++)
        {
            if (factory.craftRecycle[i] == craftId)
            {
                var last = factory.craftRecycleCursor - 1;
                factory.craftRecycle[i] = factory.craftRecycle[last];
                factory.craftRecycleCursor = last;
                break;
            }
        }
    }

    private PlanetFactory Factory => GameMain.galaxy?.PlanetById(planetId)?.factory;
}

/// <summary>
/// The client-local pool mechanics behind <see cref="CraftBinding"/> for space craft.
/// </summary>
/// <remarks>
/// Sector craft have no spatial hash in vanilla (the sector pool is global); display, colliders and
/// audio are built through <c>SpaceSector.CreateCraftDisplayComponents</c>. Logic ids stay zero so
/// fleet, targeting, ammo and destruction ticks skip the shell. Removal is the pure path only.
/// </remarks>
internal sealed class SpaceCraftReplicaPools : ILocalCraftPools
{
    public bool TryGetLocalId(int hostId, out int localId) { localId = hostId; return CraftExists(hostId); }
    public bool CraftExists(int craftId)
    {
        var sector = GameMain.spaceSector;
        return sector != null && craftId > 0 && craftId < sector.craftCursor &&
            craftId < sector.craftPool.Length && sector.craftPool[craftId].id == craftId;
    }

    public void CreateCraftShell(int craftId, in CraftState state)
    {
        var sector = GameMain.spaceSector;
        if (sector == null || craftId <= 0) return;
        EnsureCapacity(sector, craftId);
        if (sector.craftPool[craftId].id == craftId)
        {
            RemoveCraftShell(craftId);
        }
        RemoveFromRecycle(sector, craftId);
        if (craftId >= sector.craftCursor)
        {
            sector.craftCursor = craftId + 1;
        }
        ref var craft = ref sector.craftPool[craftId];
        craft.SetEmpty();
        craft.id = craftId;
        craft.protoId = state.ProtoId;
        craft.modelIndex = state.ModelIndex;
        craft.astroId = state.AstroId;
        craft.owner = state.Owner;
        craft.port = state.Port;
        craft.prototype = (ECraftProto)state.Prototype;
        craft.dynamic = state.IsDynamic;
        craft.isSpace = true;
        craft.stateFlags = state.StateFlags;
        craft.pos.x = state.PosX;
        craft.pos.y = state.PosY;
        craft.pos.z = state.PosZ;
        craft.rot.x = state.RotX;
        craft.rot.y = state.RotY;
        craft.rot.z = state.RotZ;
        craft.rot.w = state.RotW;
        craft.vel.x = state.VelX;
        craft.vel.y = state.VelY;
        craft.vel.z = state.VelZ;
        craft.combatStatId = 0;
        craft.droneId = 0;
        craft.fleetId = 0;
        craft.unitId = 0;
        craft.cAnchorId = 0;
        craft.vehicleId = 0;
        craft.vPartId = 0;
        craft.vdCockpitId = 0;
        craft.vdGyroscopeId = 0;
        craft.vdTyreId = 0;
        craft.vdSuspensionId = 0;
        craft.vdEngineId = 0;
        craft.vdWarpId = 0;
        craft.vdBatteryId = 0;
        craft.vdStorageId = 0;
        craft.vdFuelStorageId = 0;
        craft.vdTankId = 0;
        craft.vdConnectorId = 0;
        craft.vwGaussId = 0;
        craft.vwLaserId = 0;
        craft.vwCannonId = 0;
        craft.vwMissileId = 0;
        craft.vwPlasmaId = 0;
        craft.vwDisturbId = 0;
        craft.vwThrowId = 0;
        craft.vwShieldId = 0;
        craft.rigidId = 0;
        craft.ccrId = 0;
        craft.modelId = 0;
        craft.mmblockId = 0;
        craft.colliderId = 0;
        craft.audioId = 0;
        try
        {
            sector.CreateCraftDisplayComponents(craftId);
        }
        catch (System.Exception e)
        {
            NebulaeModel.Logger.Log.Warn("[authority] space craft display creation failed for " + craftId + ": " + e.Message);
        }
        WriteCombatStat(sector, craftId, in state);
        NativeCraftSimulation.Install(sector, sector.craftPool, craftId, craftId, state);
        CraftPresentation.RefreshSpace(sector, craftId);
    }

    public void WriteCraftState(int craftId, in CraftState state)
    {
        var sector = GameMain.spaceSector;
        if (sector == null || craftId <= 0 || craftId >= sector.craftPool.Length) return;
        ref var craft = ref sector.craftPool[craftId];
        if (craft.id != craftId) return;
        craft.protoId = state.ProtoId;
        craft.modelIndex = state.ModelIndex;
        craft.owner = state.Owner;
        craft.port = state.Port;
        craft.stateFlags = state.StateFlags;
        craft.astroId = state.AstroId;
        craft.pos = NativeCraftSimulation.ReconcilePosition(sector, craftId, state, craft.pos);
        craft.rot.x = state.RotX;
        craft.rot.y = state.RotY;
        craft.rot.z = state.RotZ;
        craft.rot.w = state.RotW;
        craft.vel.x = state.VelX;
        craft.vel.y = state.VelY;
        craft.vel.z = state.VelZ;
        WriteCombatStat(sector, craftId, in state);
        NativeCraftSimulation.Install(sector, sector.craftPool, craftId, craftId, state);
        CraftPresentation.RefreshSpace(sector, craftId);
    }

    public void RemoveCraftShell(int craftId)
    {
        var sector = GameMain.spaceSector;
        if (sector == null || craftId <= 0 || craftId >= sector.craftPool.Length) return;
        if (sector.craftPool[craftId].id != craftId) return;
        NativeCraftSimulation.Remove(sector, craftId);
        sector.RemoveCraftWithComponents(craftId);
    }

    private void WriteCombatStat(SpaceSector sector, int craftId, in CraftState state)
    {
        ref var craft = ref sector.craftPool[craftId];
        var skill = GameMain.data.spaceSector.skillSystem;
        if (state.HasCombatStat)
        {
            int statId = craft.combatStatId;
            if (statId <= 0 || statId >= skill.combatStats.cursor || skill.combatStats.buffer[statId].id != statId ||
                skill.combatStats.buffer[statId].objectType != (int)EObjectType.Craft ||
                skill.combatStats.buffer[statId].objectId != craftId || skill.combatStats.buffer[statId].astroId != state.AstroId)
            {
                statId = skill.combatStats.Add().id;
                craft.combatStatId = statId;
            }
            ref var stat = ref skill.combatStats.buffer[statId];
            stat.hp = state.Hp;
            stat.hpMax = state.HpMax;
            stat.hpRecover = state.HpRecover;
            stat.hpIncoming = state.HpIncoming;
            stat.astroId = state.AstroId;
            stat.originAstroId = state.AstroId;
            stat.objectType = (int)EObjectType.Craft;
            stat.objectId = craftId;
            stat.localPos = sector.craftPool[craftId].pos;
            stat.size = 1f;
        }
        else if (craft.combatStatId != 0)
        {
            var statId = craft.combatStatId;
            if (statId > 0 && statId < skill.combatStats.cursor && skill.combatStats.buffer[statId].id == statId &&
                skill.combatStats.buffer[statId].objectType == (int)EObjectType.Craft &&
                skill.combatStats.buffer[statId].objectId == craftId && skill.combatStats.buffer[statId].astroId == craft.astroId)
            {
                skill.OnRemovingSkillTarget(statId, skill.combatStats.buffer[statId].originAstroId, ETargetType.CombatStat);
                skill.combatStats.Remove(statId);
            }
            craft.combatStatId = 0;
        }
    }

    private static void EnsureCapacity(SpaceSector sector, int craftId)
    {
        while (craftId >= sector.craftCapacity)
        {
            sector.SetCraftCapacity(sector.craftCapacity * 2);
        }
    }

    private static void RemoveFromRecycle(SpaceSector sector, int craftId)
    {
        for (var i = 0; i < sector.craftRecycleCursor; i++)
        {
            if (sector.craftRecycle[i] == craftId)
            {
                var last = sector.craftRecycleCursor - 1;
                sector.craftRecycle[i] = sector.craftRecycle[last];
                sector.craftRecycleCursor = last;
                break;
            }
        }
    }
}
