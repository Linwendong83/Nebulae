using NebulaeWorld;
#region

using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>
/// The client-local pool mechanics behind <see cref="SpaceEnemyBinding"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every method here touches vanilla pools; none decides whether a write should happen — that is the
/// binding core's call. Buildings retain passive builder/connector components for native layout,
/// geometry and picking; the client skips non-unit hive logic. Units retain their native
/// <c>EnemyUnitComponent</c> for motion and picking: the hover scan reads <c>EnemyDFHiveSystem.units</c>, not
/// the
/// enemy pool, so a unit without it cannot be hovered at all — see
/// <see cref="EnsureUnitComponent"/>. Rendering, colliders and audio are built through the same
/// display path the vanilla creation uses (<c>SpaceSector.CreateEnemyDisplayComponents</c>), which is
/// why hive/relay/tinder topology shows without running any rule. Relay and tinder components
/// carry passive UI/picking facts; the client's native non-unit loops remain disabled.
/// </para>
/// <para>
/// Removal is always the pure path (<c>SpaceSector.RemoveEnemyWithComponents</c>), never
/// <c>KillEnemyFinal</c>: despawn must not generate drops, statistics or experience. The host owns
/// the single death transaction; the client only removes the mirror. That path frees the display
/// unit component with the shell (it releases <c>enemyPool[id].unitId</c> through the unit's hive), so
/// a unit that dies or returns to its formation leaves nothing behind.
/// </para>
/// </remarks>
internal sealed class SpaceEnemyReplicaPools : ISpaceEnemyPools
{
    public bool EnemyExists(int enemyId)
    {
        var sector = GameMain.spaceSector;
        return sector != null && enemyId > 0 && enemyId < sector.enemyCursor &&
            enemyId < sector.enemyPool.Length && sector.enemyPool[enemyId].id == enemyId;
    }

    public void CreateEnemyShell(int enemyId, in SpaceEnemyState state)
    {
        var sector = GameMain.spaceSector;
        if (sector == null || enemyId <= 0) return;
        EnsureCapacity(sector, enemyId);
        if (sector.enemyPool[enemyId].id == enemyId)
        {
            RemoveEnemyShell(enemyId);
        }
        RemoveFromRecycle(sector, enemyId);
        if (enemyId >= sector.enemyCursor)
        {
            sector.enemyCursor = enemyId + 1;
        }
        ref var enemy = ref sector.enemyPool[enemyId];
        enemy.SetEmpty();
        enemy.id = enemyId;
        enemy.protoId = state.ProtoId;
        enemy.modelIndex = state.ModelIndex;
        enemy.astroId = state.AstroId;
        enemy.originAstroId = state.OriginAstroId != 0 ? state.OriginAstroId : state.AstroId;
        enemy.owner = state.Owner;
        enemy.port = state.Port;
        enemy.dynamic = state.IsDynamic;
        enemy.isSpace = true;
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
        enemy.dfSCoreId = 0;
        enemy.dfSNodeId = 0;
        enemy.dfSConnectorId = 0;
        enemy.dfSReplicatorId = 0;
        enemy.dfSGammaId = 0;
        enemy.dfSTurretId = 0;
        enemy.dfTinderId = 0;
        enemy.dfRelayId = 0;
        enemy.unitId = 0;
        enemy.modelId = 0;
        enemy.mmblockId = 0;
        enemy.colliderId = 0;
        enemy.audioId = 0;
        SpaceEnemyPresentation.Install(sector, enemyId, state);
        try
        {
            sector.CreateEnemyDisplayComponents(enemyId, null, false);
        }
        catch (System.Exception e)
        {
            NebulaeModel.Logger.Log.Warn("[authority] space enemy display creation failed for " + enemyId + ": " + e.Message);
            RemoveEnemyShell(enemyId);
            throw;
        }
        WriteDisplayState(sector, enemyId, in state);
        WriteCombatStat(sector, enemyId, in state);
        EnsureUnitComponent(sector, enemyId, in state);
        NebulaeWorld.Combat.NativeEnemySimulation.Install(sector, enemyId, state);
        Multiplayer.Session?.DarkFogTurrets?.Install(sector, enemyId, state);
    }

    public void WriteEnemyState(int enemyId, in SpaceEnemyState state)
    {
        var sector = GameMain.spaceSector;
        if (sector == null || enemyId <= 0 || enemyId >= sector.enemyPool.Length) return;
        ref var enemy = ref sector.enemyPool[enemyId];
        if (enemy.id != enemyId) return;
        var presentation = Multiplayer.Session?.EnemyMotion;
        var before = presentation?.Capture(sector, enemyId,
            state.Kind == SpaceEnemyKind.SpaceUnit && state.IsDynamic && state.NativeUnit?.Length > 0) ?? default;
        enemy.protoId = state.ProtoId;
        enemy.modelIndex = state.ModelIndex;
        enemy.owner = state.Owner;
        enemy.port = state.Port;
        enemy.dynamic = state.IsDynamic;
        enemy.stateFlags = state.StateFlags;
        enemy.astroId = state.AstroId;
        enemy.originAstroId = state.OriginAstroId;
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
        SpaceEnemyPresentation.Install(sector, enemyId, state);
        WriteDisplayState(sector, enemyId, in state);
        WriteCombatStat(sector, enemyId, in state);
        EnsureUnitComponent(sector, enemyId, in state);
        NebulaeWorld.Combat.NativeEnemySimulation.Install(sector, enemyId, state);
        Multiplayer.Session?.DarkFogTurrets?.Install(sector, enemyId, state);
        presentation?.Rebase(sector, sector.enemyPool, enemyId, before, true);
    }

    /// <summary>
    /// Allocates the native motion and picking component and installs the server's behavior anchor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The vanilla hover scan (<c>SpaceColliderLogic</c>'s unit loop) walks the local star's
    /// <c>EnemyDFHiveSystem.units</c>, not the enemy pool: a shell without this component is invisible
    /// to the cursor even though it renders and fights, which is why on a client only the hive
    /// formation preview (<c>units[port] == 1</c>) can be hovered — and an activated unit has left
    /// that formation. <c>EnemyDFHiveSystem.GameTickLogic</c>,
    /// runs unit behavior and sensing locally after its inputs are installed. Non-unit hive
    /// logic and shared lifecycle commits remain server-owned.
    /// </para>
    /// <para>
    /// Creation waits for the hive the unit belongs to — the same hive the vanilla creation resolves
    /// from <c>originAstroId</c> — because the component lives in that hive's pool. The binding does
    /// not defer unit records; the motion pass retries component binding when the hive becomes ready.
    /// </para>
    /// </remarks>
    internal static void EnsureUnitComponent(SpaceSector sector, int enemyId, in SpaceEnemyState state)
    {
        if (state.Kind != SpaceEnemyKind.SpaceUnit) return;
        ref var enemy = ref sector.enemyPool[enemyId];
        if (state.NativeUnit?.Length > 0)
        {
            var origin = sector.GetHiveByAstroId(state.OriginAstroId);
            if (origin == null) return;
            if (state.DecodedNativeUnit.HasValue
                ? NebulaeWorld.Combat.NativeEnemySimulation.BindDecodedUnit(origin.units, ref enemy, state.DecodedNativeUnit.Value, 0)
                : NebulaeWorld.Combat.NativeEnemySimulation.BindUnit(origin.units, ref enemy, state.NativeUnit, 0))
                WriteUnitState(sector, enemyId, state);
            return;
        }
        if (enemy.unitId > 0)
        {
            WriteUnitState(sector, enemyId, state);
            return;
        }
        // Only the origin hive, exactly like the vanilla creation and the removal path both resolve it:
        // a component placed in any other hive's pool would never be freed with the shell.
        var hive = sector.GetHiveByAstroId(state.OriginAstroId);
        if (hive == null) return;
        try
        {
            var unitId = hive.NewEnemyUnitComponent(enemyId);
            if (unitId <= 0 || enemy.unitId != unitId || hive.units.buffer[unitId].id != unitId)
            {
                NebulaeModel.Logger.Log.Warn("[authority] space enemy unit component mismatch for " + enemyId);
                return;
            }
            WriteUnitState(sector, enemyId, state);
        }
        catch (System.Exception e)
        {
            NebulaeModel.Logger.Log.Warn("[authority] space enemy unit component failed for " + enemyId + ": " + e.Message);
        }
    }

    private static void WriteUnitState(SpaceSector sector, int enemyId, in SpaceEnemyState state)
    {
        ref var enemy = ref sector.enemyPool[enemyId];
        if (enemy.unitId <= 0) return;
        var hive = sector.GetHiveByAstroId(enemy.originAstroId);
        if (hive == null || enemy.unitId >= hive.units.cursor) return;
        ref var unit = ref hive.units.buffer[enemy.unitId];
        if (unit.id != enemy.unitId) return;
        unit.behavior = (EEnemyBehavior)state.UnitBehavior;
        unit.stateTick = state.UnitStateTick;
        unit.disturbValue = state.UnitDisturb;
        unit.level = state.Level;
        unit.anim = state.UnitAnimation;
        unit.steering = state.UnitSteering;
        unit.speed = state.UnitSpeed;
    }

    public static void WriteDisplayState(SpaceSector sector, int enemyId, in SpaceEnemyState state)
    {
        // Hive shaders use builder animation time and connector length to reveal geometry.
        // A zeroed animation pool renders only scattered fragments of an otherwise valid hive.
        sector.enemyAnimPool[enemyId] = new AnimData
        {
            time = state.AnimationTime,
            prepare_length = state.PrepareLength,
            working_length = state.WorkingLength,
            state = state.AnimationState,
            power = state.AnimationPower
        };
        RefreshPose(sector, enemyId);
    }

    internal static void RefreshPose(SpaceSector sector, int enemyId, bool? starmap = null)
    {
        ref var enemy = ref sector.enemyPool[enemyId];
        var model = sector.model;
        if (model?.gpuiManager == null || enemy.modelId <= 0) return;
        var renderer = model.gpuiManager.objectRenderers[enemy.modelIndex];
        if (renderer == null || enemy.modelId >= renderer.instPool.Length) return;
        var position = enemy.pos;
        var rotation = enemy.rot;
        if (enemy.astroId == 0)
        {
            SpaceEnemyPresentation.RenderPose(position, rotation, starmap ?? (UIGame.viewMode == EViewMode.Starmap && UIGame.viewModeReady),
                GameMain.data.relativePos, GameMain.data.relativeRot, GameMain.data.starmapViewPos, out position, out rotation);
        }
        ref var instance = ref renderer.instPool[enemy.modelId];
        instance.astroId = (uint)enemy.astroId;
        instance.posx = (float)position.x;
        instance.posy = (float)position.y;
        instance.posz = (float)position.z;
        instance.rotx = rotation.x;
        instance.roty = rotation.y;
        instance.rotz = rotation.z;
        instance.rotw = rotation.w;
        if (enemy.unitId > 0) model.enemyUnitsDirty = true;
    }

    public void RemoveEnemyShell(int enemyId)
    {
        var sector = GameMain.spaceSector;
        if (sector == null || enemyId <= 0 || enemyId >= sector.enemyPool.Length) return;
        if (sector.enemyPool[enemyId].id != enemyId) return;
        SpaceEnemyPresentation.Forget(sector, enemyId);
        Multiplayer.Session?.EnemyMotion?.Remove(sector, enemyId);
        NebulaeWorld.Combat.NativeEnemySimulation.Remove(sector, enemyId);
        var statId = sector.enemyPool[enemyId].combatStatId;
        var stats = sector.skillSystem.combatStats;
        if (statId > 0 && (statId >= stats.cursor || stats.buffer[statId].id != statId ||
            stats.buffer[statId].objectType != (int)EObjectType.Enemy || stats.buffer[statId].objectId != enemyId ||
            stats.buffer[statId].astroId != sector.enemyPool[enemyId].astroId))
            sector.enemyPool[enemyId].combatStatId = 0;
        sector.RemoveEnemyWithComponents(enemyId);
    }

    /// <summary>
    /// Spawns the vanilla death presentation for a live shell: the wreckage fragments the Kill path
    /// would have produced.
    /// </summary>
    /// <remarks>
    /// The presentation half of <c>SpaceSector.KillEnemyFinal</c> without the rule half. Vanilla
    /// plays the wreckage for any enemy with a valid position whose model has one, and plays no
    /// death audio in space at all. Nothing here writes a pool, so the host keeps the single death
    /// transaction and a shell presented twice is only a doubled effect.
    /// </remarks>
    public void PlayDeathPresentation(int enemyId)
    {
        var sector = GameMain.spaceSector;
        if (sector == null || enemyId <= 0 || enemyId >= sector.enemyPool.Length) return;
        ref var enemy = ref sector.enemyPool[enemyId];
        if (enemy.id != enemyId) return;
        // Vanilla's own gate: a position the sector cannot place has no wreckage to place either.
        if (!enemy.positionIsValid) return;
        try
        {
            var modelProto = LDB.models.Select(enemy.modelIndex);
            if (modelProto == null) return;
            if (SpaceSector.PrefabDescByModelIndex[
                modelProto.RuinId != 0 ? modelProto.RuinId : modelProto.ID]?.wreckagePrefab == null) return;
            var model = sector.model;
            if (model == null) return;
            model.AddWreckage(modelProto, 0, enemy.astroId, ref enemy.pos, ref enemy.rot,
                ref enemy.vel);
        }
        catch (System.Exception e)
        {
            NebulaeModel.Logger.Log.Warn("[authority] space enemy death presentation failed for " +
                enemyId + ": " + e.Message);
        }
    }

    private void WriteCombatStat(SpaceSector sector, int enemyId, in SpaceEnemyState state)
    {
        ref var enemy = ref sector.enemyPool[enemyId];
        var skill = GameMain.data.spaceSector.skillSystem;
        if (state.HasCombatStat)
        {
            int statId = enemy.combatStatId;
            if (statId <= 0 || statId >= skill.combatStats.cursor || skill.combatStats.buffer[statId].id != statId ||
                skill.combatStats.buffer[statId].objectType != (int)EObjectType.Enemy ||
                skill.combatStats.buffer[statId].objectId != enemyId || skill.combatStats.buffer[statId].astroId != state.AstroId)
            {
                statId = skill.combatStats.Add().id;
                enemy.combatStatId = statId;
            }
            ref var stat = ref skill.combatStats.buffer[statId];
            stat.hp = state.Hp;
            stat.hpMax = state.HpMax;
            stat.hpRecover = state.HpRecover;
            stat.hpIncoming = state.HpIncoming;
            stat.astroId = state.AstroId;
            stat.originAstroId = state.OriginAstroId;
            stat.objectType = (int)EObjectType.Enemy;
            stat.objectId = enemyId;
            EnemyHealthBar.Update(ref stat, in enemy, true);
        }
        else if (enemy.combatStatId != 0)
        {
            var statId = enemy.combatStatId;
            if (statId > 0 && statId < skill.combatStats.cursor && skill.combatStats.buffer[statId].id == statId &&
                skill.combatStats.buffer[statId].objectType == (int)EObjectType.Enemy &&
                skill.combatStats.buffer[statId].objectId == enemyId && skill.combatStats.buffer[statId].astroId == enemy.astroId)
            {
                skill.OnRemovingSkillTarget(statId, skill.combatStats.buffer[statId].originAstroId, ETargetType.CombatStat);
                skill.combatStats.Remove(statId);
            }
            enemy.combatStatId = 0;
        }
    }

    private static void EnsureCapacity(SpaceSector sector, int enemyId)
    {
        while (enemyId >= sector.enemyCapacity)
        {
            sector.SetEnemyCapacity(sector.enemyCapacity * 2);
        }
    }

    private static void RemoveFromRecycle(SpaceSector sector, int enemyId)
    {
        for (var i = 0; i < sector.enemyRecycleCursor; i++)
        {
            if (sector.enemyRecycle[i] == enemyId)
            {
                var last = sector.enemyRecycleCursor - 1;
                sector.enemyRecycle[i] = sector.enemyRecycle[last];
                sector.enemyRecycleCursor = last;
                break;
            }
        }
    }
}
