using NebulaeWorld;
#region

using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using System;
using NebulaeModel.DataStructures;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>
/// The client-local pool mechanics behind <see cref="GroundEnemyBinding"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every method here touches vanilla pools; none decides whether a write should happen — that is the
/// binding core's call. Building logic component ids
/// (builder/turret/shield/connector/replicator) stay zero, so the vanilla AI, hatred, attack and
/// base-manufacture ticks skip it. A unit retains its <c>EnemyUnitComponent</c> for native local motion,
/// picking and animation. Shared AI and combat decisions remain on the server. Rendering, colliders,
/// audio and the spatial hash are built through the same display path the vanilla creation uses
/// (<c>CreateEnemyDisplayComponents</c>), which is why "主池、空间哈希、物理/渲染引用的重绑定" holds
/// without running any rule.
/// </para>
/// <para>
/// Removal is always the pure path (<c>RemoveEnemyWithComponents</c>), never
/// <c>KillEnemyFinally</c>: despawn must not generate drops, statistics or experience. The host owns
/// the single death transaction; the client only removes the mirror. That path frees the display
/// unit component with the shell (<c>PlanetFactory.RemoveEnemyWithComponents</c> releases
/// <c>enemyPool[id].unitId</c>), so a unit that dies or returns to its formation leaves nothing behind.
/// </para>
/// </remarks>
internal sealed class GroundEnemyReplicaPools : IGroundEnemyPools
{
    private readonly int planetId;
    private readonly ReplicaPresentationWrites presentationWrites = new();
    private readonly Action<int, bool, bool> writePose;
    private PlanetFactory batchFactory;

    public GroundEnemyReplicaPools(int planetId)
    {
        this.planetId = planetId;
        writePose = WritePose;
    }

    internal void BeginBatch(PlanetFactory factory) => batchFactory = factory;
    internal bool BatchOpen => batchFactory != null;
    internal void CancelBatch() { presentationWrites.Clear(); batchFactory = null; }
    internal void EndBatch()
    {
        try { presentationWrites.Flush(writePose); }
        finally
        {
            batchFactory = null;
        }
    }
    private void WritePose(int id, bool moved, bool rotated)
    {
        var factory = Factory;
        if (factory != null && id > 0 && id < factory.enemyCursor && factory.enemyPool[id].id == id)
            RefreshPose(factory, id, moved, rotated);
    }

    public bool EnemyExists(int enemyId)
    {
        var factory = Factory;
        return factory != null && enemyId > 0 && enemyId < factory.enemyCursor &&
            enemyId < factory.enemyPool.Length && factory.enemyPool[enemyId].id == enemyId;
    }

    public void CreateEnemyShell(int enemyId, in GroundEnemyState state)
    {
        Multiplayer.Session?.AuthorityRuntime?.DarkFogReplica?.MarkSearchDirty(planetId);
        var factory = Factory;
        if (factory == null || enemyId <= 0) return;
        EnsureCapacity(factory, enemyId);
        if (factory.enemyPool[enemyId].id == enemyId)
        {
            RemoveEnemyShell(enemyId);
        }
        RemoveFromRecycle(factory, enemyId);
        if (enemyId >= factory.enemyCursor)
        {
            factory.enemyCursor = enemyId + 1;
        }
        ref var enemy = ref factory.enemyPool[enemyId];
        enemy.SetEmpty();
        enemy.id = enemyId;
        enemy.protoId = state.ProtoId;
        enemy.modelIndex = state.ModelIndex;
        enemy.astroId = planetId;
        enemy.originAstroId = state.OriginAstroId != 0 ? state.OriginAstroId : planetId;
        enemy.owner = state.Owner;
        enemy.port = state.Port;
        enemy.dynamic = state.IsDynamic;
        enemy.isSpace = false;
        enemy.localized = true;
        enemy.stateFlags = state.StateFlags;
        enemy.pos.x = state.PosX;
        enemy.pos.y = state.PosY;
        enemy.pos.z = state.PosZ;
        enemy.rot.x = state.RotX;
        enemy.rot.y = state.RotY;
        enemy.rot.z = state.RotZ;
        enemy.rot.w = state.RotW;
        enemy.vel.x = state.VelX;
        enemy.vel.y = state.VelY;
        enemy.vel.z = state.VelZ;
        enemy.combatStatId = 0;
        enemy.builderId = 0;
        enemy.dfGBaseId = 0;
        enemy.dfGConnectorId = 0;
        enemy.dfGReplicatorId = 0;
        enemy.dfGTurretId = 0;
        enemy.dfGShieldId = 0;
        enemy.unitId = 0;
        enemy.modelId = 0;
        enemy.mmblockId = 0;
        enemy.colliderId = 0;
        enemy.audioId = 0;
        if (enemy.dynamic)
        {
            enemy.hashAddress = factory.hashSystemDynamic.AddObjectToBucket(enemyId, enemy.pos, EObjectType.Enemy);
        }
        else
        {
            enemy.hashAddress = factory.hashSystemStatic.AddObjectToBucket(enemyId, enemy.pos, EObjectType.Enemy);
        }
        try
        {
            factory.CreateEnemyDisplayComponents(enemyId);
        }
        catch (System.Exception e)
        {
            NebulaeModel.Logger.Log.Warn("[authority] ground enemy display creation failed for " + enemyId + ": " + e.Message);
            RemoveEnemyShell(enemyId);
            throw;
        }
        WriteCombatStat(factory, enemyId, in state);
        factory.enemyAnimPool[enemyId] = new AnimData
        {
            time = state.AnimationTime,
            prepare_length = state.PrepareLength,
            working_length = state.WorkingLength,
            state = state.AnimationState,
            power = state.AnimationPower
        };
        EnsureUnitComponent(factory, enemyId, in state);
        RefreshPose(factory, enemyId, false, false);
        NebulaeWorld.Combat.NativeEnemySimulation.Install(factory, enemyId, state);
        Multiplayer.Session?.DarkFogTurrets?.Install(factory, enemyId, state);
    }

    public void WriteEnemyState(int enemyId, in GroundEnemyState state)
    {
        var factory = Factory;
        if (factory == null || enemyId <= 0 || enemyId >= factory.enemyPool.Length) return;
        ref var enemy = ref factory.enemyPool[enemyId];
        if (enemy.id != enemyId) return;
        if (enemy.owner != state.Owner) Multiplayer.Session?.AuthorityRuntime?.DarkFogReplica?.MarkSearchDirty(planetId);
        var presentation = Multiplayer.Session?.EnemyMotion;
        var before = presentation?.Capture(factory, enemyId,
            state.Kind == GroundEnemyKind.GroundUnit && state.IsDynamic && state.NativeUnit?.Length > 0) ?? default;
        enemy.protoId = state.ProtoId;
        enemy.modelIndex = state.ModelIndex;
        enemy.owner = state.Owner;
        enemy.port = state.Port;
        enemy.stateFlags = state.StateFlags;
        var moved = enemy.pos.x != state.PosX || enemy.pos.y != state.PosY || enemy.pos.z != state.PosZ;
        var rotated = enemy.rot.x != state.RotX || enemy.rot.y != state.RotY || enemy.rot.z != state.RotZ || enemy.rot.w != state.RotW;
        enemy.pos.x = state.PosX;
        enemy.pos.y = state.PosY;
        enemy.pos.z = state.PosZ;
        enemy.rot.x = state.RotX;
        enemy.rot.y = state.RotY;
        enemy.rot.z = state.RotZ;
        enemy.rot.w = state.RotW;
        enemy.vel.x = state.VelX;
        enemy.vel.y = state.VelY;
        enemy.vel.z = state.VelZ;
        factory.enemyAnimPool[enemyId] = new AnimData
        {
            time = state.AnimationTime,
            prepare_length = state.PrepareLength,
            working_length = state.WorkingLength,
            state = state.AnimationState,
            power = state.AnimationPower
        };
        WriteCombatStat(factory, enemyId, in state);
        EnsureUnitComponent(factory, enemyId, in state);
        if (batchFactory != null) presentationWrites.Add(enemyId, moved, rotated);
        else RefreshPose(factory, enemyId, moved, rotated);
        NebulaeWorld.Combat.NativeEnemySimulation.Install(factory, enemyId, state);
        Multiplayer.Session?.DarkFogTurrets?.Install(factory, enemyId, state);
        presentation?.Rebase(factory, factory.enemyPool, enemyId, before, false);
    }

    /// <summary>
    /// Allocates the native component used for local motion and picking, and installs the server's behavior anchor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The vanilla hover scan (<c>RaycastLogic</c>'s unit loop) walks
    /// <c>EnemyDFGroundSystem.units</c>, not the enemy pool: a shell without this component is
    /// invisible to the cursor even though it renders and fights, which is why on a client only the
    /// formation preview (<c>units[port] == 1</c>) can be hovered — and an activated unit has left
    /// that formation. World AI entry points
    /// (<c>EnemyDFGroundSystem.GameTickLogic_Unit</c> and <c>GameLogic._enemy_ground_unit_parallel</c>)
    /// run on installed native unit inputs; lifecycle and damage commits remain server-owned.
    /// </para>
    /// <para>
    /// Creation waits for the base component the host links the unit to: both
    /// <c>NewEnemyUnitComponent</c> and the vanilla brief panel index <c>bases[owner]</c>, and a unit
    /// whose base has not been mirrored yet would read out of bounds or a null base. The motion pass
    /// retries component binding after the dependency arrives, even when no new unit state is published.
    /// </para>
    /// </remarks>
    internal static void EnsureUnitComponent(PlanetFactory factory, int enemyId, in GroundEnemyState state)
    {
        if (state.Kind != GroundEnemyKind.GroundUnit) return;
        ref var enemy = ref factory.enemyPool[enemyId];
        if (state.NativeUnit?.Length > 0)
        {
            if (!BaseComponentExists(factory, enemy.owner)) return;
            if (state.DecodedNativeUnit.HasValue
                ? NebulaeWorld.Combat.NativeEnemySimulation.BindDecodedUnit(factory.enemySystem.units, ref enemy, state.DecodedNativeUnit.Value, factory.planetId)
                : NebulaeWorld.Combat.NativeEnemySimulation.BindUnit(factory.enemySystem.units, ref enemy, state.NativeUnit, factory.planetId))
                WriteUnitState(factory, enemyId, state);
            return;
        }
        if (enemy.unitId > 0)
        {
            WriteUnitState(factory, enemyId, state);
            return;
        }
        if (!BaseComponentExists(factory, enemy.owner)) return;
        try
        {
            var unitId = factory.enemySystem.NewEnemyUnitComponent(enemyId);
            if (unitId <= 0 || enemy.unitId != unitId || factory.enemySystem.units.buffer[unitId].id != unitId)
            {
                NebulaeModel.Logger.Log.Warn("[authority] ground enemy unit component mismatch for " + enemyId);
                return;
            }
            WriteUnitState(factory, enemyId, state);
        }
        catch (System.Exception e)
        {
            NebulaeModel.Logger.Log.Warn("[authority] ground enemy unit component failed for " + enemyId + ": " + e.Message);
        }
    }

    private static void WriteUnitState(PlanetFactory factory, int enemyId, in GroundEnemyState state)
    {
        ref var enemy = ref factory.enemyPool[enemyId];
        if (enemy.unitId <= 0) return;
        var units = factory.enemySystem.units;
        if (enemy.unitId >= units.cursor) return;
        ref var unit = ref units.buffer[enemy.unitId];
        if (unit.id != enemy.unitId) return;
        unit.behavior = (EEnemyBehavior)state.UnitBehavior;
        unit.stateTick = state.UnitStateTick;
        unit.level = state.Level;
        unit.anim = state.UnitAnimation;
        unit.disturbValue = state.UnitDisturb;
        unit.steering = state.UnitSteering;
        unit.speed = state.UnitSpeed;
    }

    /// <summary>
    /// True when the base component named by <paramref name="owner"/> is mirrored, growing the pool
    /// first so the vanilla <c>bases[owner]</c> reads stay in bounds (the same capacity rule
    /// <c>DarkFogReplicaBinding.ApplyBase</c> applies when it installs a base).
    /// </summary>
    private static bool BaseComponentExists(PlanetFactory factory, int owner)
    {
        if (owner <= 0) return true;
        var bases = factory.enemySystem.bases;
        if (bases.buffer == null) return false;
        if (bases.capacity <= owner) bases.SetCapacity(System.Math.Max(owner + 1, bases.capacity * 2));
        return bases.buffer[owner]?.id == owner;
    }

    internal static void RefreshPhysics(PlanetFactory factory, int enemyId, bool moved, bool rotated)
    {
        if (!moved && !rotated) return;
        ref var enemy = ref factory.enemyPool[enemyId];
        if (moved)
        {
            var hash = enemy.dynamic ? factory.hashSystemDynamic : factory.hashSystemStatic;
            enemy.hashAddress = hash.UpdateObjectHashAddress(enemy.hashAddress, enemyId, enemy.pos, EObjectType.Enemy);
            var stats = GameMain.spaceSector?.skillSystem?.combatStats;
            if (stats != null && enemy.combatStatId > 0 && enemy.combatStatId < stats.cursor &&
                stats.buffer[enemy.combatStatId].id == enemy.combatStatId)
                EnemyHealthBar.Update(ref stats.buffer[enemy.combatStatId], in enemy, false);
        }
        if ((moved || rotated) && enemy.colliderId > 0)
        {
            var colliders = PlanetFactory.PrefabDescByModelIndex[enemy.modelIndex]?.colliders;
            var colliderId = enemy.colliderId;
            if (colliders != null)
                for (var i = colliders.Length - 1; i >= 0 && colliderId > 0; i--)
                {
                    var old = factory.planet.physics.GetColliderData(colliderId);
                    if (old.objType != EObjectType.Enemy || old.objId != enemyId) break;
                    factory.planet.physics.SetColliderData(colliderId, colliders[i].BindToObject(enemyId, old.link, EObjectType.Enemy, enemy.pos, enemy.rot));
                    colliderId = old.link;
                }
        }
    }

    internal static void RefreshPose(PlanetFactory factory, int enemyId, bool moved, bool rotated)
    {
        RefreshPhysics(factory, enemyId, moved, rotated);
        ref var enemy = ref factory.enemyPool[enemyId];
        var model = factory.planet.factoryModel;
        var renderers = model?.gpuiManager?.objectRenderers;
        if (renderers == null || enemy.modelId <= 0 || enemy.modelIndex >= renderers.Length) return;
        var renderer = renderers[enemy.modelIndex];
        if (renderer == null || enemy.modelId >= renderer.instPool.Length) return;
        ref var instance = ref renderer.instPool[enemy.modelId];
        // Compare the actual render instance as well as the logical movement flags: a newly
        // loaded model still needs its first pose, while an unchanged model needs no GPU upload.
        var changed = instance.posx != (float)enemy.pos.x || instance.posy != (float)enemy.pos.y || instance.posz != (float)enemy.pos.z ||
            instance.rotx != enemy.rot.x || instance.roty != enemy.rot.y || instance.rotz != enemy.rot.z || instance.rotw != enemy.rot.w;
        if (changed)
        {
            instance.posx = (float)enemy.pos.x; instance.posy = (float)enemy.pos.y; instance.posz = (float)enemy.pos.z;
            instance.rotx = enemy.rot.x; instance.roty = enemy.rot.y; instance.rotz = enemy.rot.z; instance.rotw = enemy.rot.w;
        }
        if (renderer is DynamicRenderer dynamicRenderer && enemy.modelId < dynamicRenderer.extraPool.Length &&
            enemy.unitId > 0 && enemy.unitId < factory.enemySystem.units.cursor)
        {
            ref var unit = ref factory.enemySystem.units.buffer[enemy.unitId];
            ref var extra = ref dynamicRenderer.extraPool[enemy.modelId];
            if (extra.x != unit.anim || extra.y != unit.disturbValue || extra.z != unit.steering || extra.w != unit.speed)
            {
                extra = new UnityEngine.Vector4(unit.anim, unit.disturbValue, unit.steering, unit.speed);
                changed = true;
            }
        }
        if (changed) model.enemyUnitsDirty = true;
    }
    public void RemoveEnemyShell(int enemyId)
    {
        Multiplayer.Session?.AuthorityRuntime?.DarkFogReplica?.MarkSearchDirty(planetId);
        presentationWrites.FlushOne(enemyId, writePose);
        var factory = Factory;
        if (factory == null || enemyId <= 0 || enemyId >= factory.enemyPool.Length) return;
        if (factory.enemyPool[enemyId].id != enemyId) return;
        Multiplayer.Session?.EnemyMotion?.Remove(factory, enemyId);
        NebulaeWorld.Combat.NativeEnemySimulation.Remove(factory, enemyId);
        var statId = factory.enemyPool[enemyId].combatStatId;
        var stats = GameMain.spaceSector.skillSystem.combatStats;
        if (statId > 0 && (statId >= stats.cursor || stats.buffer[statId].id != statId ||
            stats.buffer[statId].objectId != enemyId || stats.buffer[statId].astroId != planetId))
            factory.enemyPool[enemyId].combatStatId = 0;
        // The Base stream owns the presentation base, independently of this enemy shell.
        // Native removal must still free imported builder/unit components, but never that base.
        factory.enemyPool[enemyId].dfGBaseId = 0;
        factory.RemoveEnemyWithComponents(enemyId);
    }

    /// <summary>
    /// Spawns the vanilla death presentation for a live shell: the wreckage fragments the Kill path
    /// would have produced, and the death audio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the presentation half of <c>PlanetFactory.KillEnemyFinally</c> without the rule half.
    /// Vanilla plays the wreckage for any enemy whose model has one while the factory is loaded, and
    /// the 169 (unit) / 170 (structure) death audio for a based enemy on the locally loaded planet;
    /// the drops, statistics and experience around them stay with the host's single death
    /// transaction. Nothing here writes a pool, so a shell presented twice is only a doubled effect,
    /// never a doubled death.
    /// </para>
    /// <para>
    /// The killing blow's impact direction is not replicated, so the fragments inherit only the
    /// enemy's own velocity: a default <c>ImpactData</c> has mass 0, which is exactly the branch
    /// vanilla skips. A growth-type wreckage (<c>DFGBuilding</c>) is skipped because a shell carries
    /// no builder progress for it to render. The audio holder lives in the sector skill system,
    /// which vanilla ticks from <c>GameLogic.LateUpdate</c> on every machine.
    /// </para>
    /// </remarks>
    public void PlayDeathPresentation(int enemyId)
    {
        presentationWrites.FlushOne(enemyId, writePose);
        var factory = Factory;
        if (factory == null || enemyId <= 0 || enemyId >= factory.enemyPool.Length) return;
        ref var enemy = ref factory.enemyPool[enemyId];
        if (enemy.id != enemyId) return;
        // The wreckage is a scene object: an unloaded factory has no model to hang it on.
        if (!factory.planet.factoryLoaded) return;
        try
        {
            var modelProto = LDB.models.Select(enemy.modelIndex);
            if (modelProto == null) return;
            var prefabDesc = PlanetFactory.PrefabDescByModelIndex[
                modelProto.RuinId != 0 ? modelProto.RuinId : modelProto.ID];
            var wreckagePrefab = prefabDesc?.wreckagePrefab;
            if (wreckagePrefab == null) return;
            var fragments = prefabDesc.wreckageFragments;
            if (fragments == null || fragments.Length == 0) return;
            var stateIndex = 0;
            if (fragments.Length > 1)
            {
                stateIndex = (int)(UnityEngine.Random.value * fragments.Length);
                if (stateIndex >= fragments.Length) stateIndex = fragments.Length - 1;
            }
            if (wreckagePrefab.transformType == WreckageHandler.ETransformType.None)
            {
                var impact = new ImpactData();
                factory.planet.factoryModel.AddWreckage(modelProto, stateIndex, enemy.pos, ref enemy.rot,
                    ref enemy.vel, ref impact);
            }
            if (enemy.owner <= 0 || factory != GameMain.data.localLoadedPlanetFactory) return;
            var protoId = enemy.dynamic ? 169 : 170;
            if (LDB.audios.Select(protoId) == null) return;
            ref var holder = ref factory.skillSystem.audio.AddPlanetAudio(protoId,
                enemy.dynamic ? 2.5f : 3.5f, factory.planet.astroId, enemy.pos);
            if (enemy.dynamic)
            {
                holder.radius0 = 80f;
                holder.radius1 = 150f;
                holder.falloff = 1f;
            }
            else
            {
                holder.multiplier = 2f;
                holder.radius0 = 130f;
                holder.radius1 = 230f;
                holder.falloff = 0.8f;
            }
        }
        catch (System.Exception e)
        {
            NebulaeModel.Logger.Log.Warn("[authority] ground enemy death presentation failed for " +
                enemyId + ": " + e.Message);
        }
    }

    private void WriteCombatStat(PlanetFactory factory, int enemyId, in GroundEnemyState state)
    {
        ref var enemy = ref factory.enemyPool[enemyId];
        var skill = GameMain.data.spaceSector.skillSystem;
        if (state.HasCombatStat)
        {
            int statId = enemy.combatStatId;
            if (statId <= 0 || statId >= skill.combatStats.cursor || skill.combatStats.buffer[statId].id != statId ||
                skill.combatStats.buffer[statId].objectType != (int)EObjectType.Enemy ||
                skill.combatStats.buffer[statId].objectId != enemyId || skill.combatStats.buffer[statId].astroId != planetId)
            {
                statId = skill.combatStats.Add().id;
                enemy.combatStatId = statId;
            }
            ref var stat = ref skill.combatStats.buffer[statId];
            stat.hp = state.Hp;
            stat.hpMax = state.HpMax;
            stat.hpRecover = state.HpRecover;
            stat.hpIncoming = state.HpIncoming;
            stat.astroId = stat.originAstroId = planetId;
            stat.objectType = (int)EObjectType.Enemy;
            stat.objectId = enemyId;
            EnemyHealthBar.Update(ref stat, in enemy, false);
        }
        else if (enemy.combatStatId != 0)
        {
            var statId = enemy.combatStatId;
            if (statId > 0 && statId < skill.combatStats.cursor && skill.combatStats.buffer[statId].id == statId &&
                skill.combatStats.buffer[statId].objectType == (int)EObjectType.Enemy &&
                skill.combatStats.buffer[statId].objectId == enemyId && skill.combatStats.buffer[statId].astroId == planetId)
            {
                skill.OnRemovingSkillTarget(statId, skill.combatStats.buffer[statId].originAstroId, ETargetType.CombatStat);
                skill.combatStats.Remove(statId);
            }
            enemy.combatStatId = 0;
        }
    }

    private static void EnsureCapacity(PlanetFactory factory, int enemyId)
    {
        while (enemyId >= factory.enemyCapacity)
        {
            factory.SetEnemyCapacity(factory.enemyCapacity * 2);
        }
    }

    private static void RemoveFromRecycle(PlanetFactory factory, int enemyId)
    {
        for (var i = 0; i < factory.enemyRecycleCursor; i++)
        {
            if (factory.enemyRecycle[i] == enemyId)
            {
                var last = factory.enemyRecycleCursor - 1;
                factory.enemyRecycle[i] = factory.enemyRecycle[last];
                factory.enemyRecycleCursor = last;
                break;
            }
        }
    }

    private PlanetFactory Factory => batchFactory ?? GameMain.galaxy?.PlanetById(planetId)?.factory;
}
