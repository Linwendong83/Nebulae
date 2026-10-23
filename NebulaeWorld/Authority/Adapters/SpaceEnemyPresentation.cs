using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Combat;
using UnityEngine;

namespace NebulaeWorld.Authority.Adapters;

/// <summary>Passive native picking/UI components and view-dependent poses. Never ticks their rules.</summary>
internal static class SpaceEnemyPresentation
{
    private sealed class Entries
    {
        internal EnemyData[] Pool;
        internal readonly Dictionary<int, SpaceEnemyState> Mobile = new();
        internal readonly Dictionary<int, SpaceEnemyState> Buildings = new();
        internal readonly HashSet<int> Travelers = new();
    }

    private static readonly ConditionalWeakTable<SpaceSector, Entries> worlds = new();
    private static readonly AccessTools.FieldRef<EnemyDFHiveSystem, Action<int, int>> builderCreated =
        AccessTools.FieldRefAccess<EnemyDFHiveSystem, Action<int, int>>("onBuilderCreate");
    private static readonly AccessTools.FieldRef<EnemyDFHiveSystem, Action<int>> builderRemoved =
        AccessTools.FieldRefAccess<EnemyDFHiveSystem, Action<int>>("onBuilderRemove");

    private static Entries For(SpaceSector sector)
    {
        var entries = worlds.GetOrCreateValue(sector);
        if (!ReferenceEquals(entries.Pool, sector.enemyPool))
        {
            // Native capacity growth replaces the array while preserving every live slot.
            var retained = entries.Pool != null && sector.enemyPool.Length >= entries.Pool.Length;
            if (retained)
                foreach (var pair in entries.Mobile)
                {
                    ref var old = ref entries.Pool[pair.Key]; ref var current = ref sector.enemyPool[pair.Key];
                    if (old.id == current.id && old.originAstroId == current.originAstroId &&
                        old.dfRelayId == current.dfRelayId && old.dfTinderId == current.dfTinderId) continue;
                    retained = false; break;
                }
            if (retained)
                foreach (var pair in entries.Buildings)
                {
                    ref var old = ref entries.Pool[pair.Key]; ref var current = ref sector.enemyPool[pair.Key];
                    if (old.id == current.id && old.originAstroId == current.originAstroId && old.builderId == current.builderId) continue;
                    retained = false; break;
                }
            entries.Pool = sector.enemyPool;
            if (!retained) { entries.Mobile.Clear(); entries.Buildings.Clear(); entries.Travelers.Clear(); }
        }
        return entries;
    }

    internal static void Install(SpaceSector sector, int id, in SpaceEnemyState state)
    {
        if (state.Kind == SpaceEnemyKind.SpaceUnit) return;
        var entries = For(sector);
        var hive = sector.GetHiveByAstroId(state.OriginAstroId);
        if (state.Kind is not (SpaceEnemyKind.SpaceRelay or SpaceEnemyKind.SpaceTinder))
        {
            entries.Buildings[id] = state;
            if (hive != null) BindBuilding(sector, hive, id, state);
            return;
        }
        entries.Mobile[id] = state;
        if (state.AstroId == 0) entries.Travelers.Add(id); else entries.Travelers.Remove(id);
        if (hive != null) Bind(sector, hive, id, state);
    }

    internal static void HiveReady(SpaceSector sector, EnemyDFHiveSystem hive)
    {
        var entries = For(sector);
        foreach (var pair in entries.Buildings)
            if (pair.Value.OriginAstroId == hive.hiveAstroId && sector.enemyPool[pair.Key].id == pair.Key)
                BindBuilding(sector, hive, pair.Key, pair.Value);
        foreach (var pair in entries.Mobile)
            if ((pair.Value.OriginAstroId == hive.hiveAstroId || pair.Value.Activity.TargetAstroId == hive.hiveAstroId) &&
                sector.enemyPool[pair.Key].id == pair.Key && sector.GetHiveByAstroId(pair.Value.OriginAstroId) is { } origin)
                Bind(sector, origin, pair.Key, pair.Value);
    }

    private static void BindBuilding(SpaceSector sector, EnemyDFHiveSystem hive, int id, in SpaceEnemyState state)
    {
        var index = state.BuilderIndex;
        var patterns = HivePatternStorage.Read(hive);
        if (index <= 0 || patterns == null || index >= patterns.Length) return;
        ref var enemy = ref sector.enemyPool[id];
        ref var pattern = ref patterns[index];
        var added = enemy.builderId <= 0 || enemy.builderId >= hive.builders.cursor || hive.builders.buffer[enemy.builderId].enemyId != id;
        if (added) enemy.builderId = hive.builders.Add().id;
        ref var builder = ref hive.builders.buffer[enemy.builderId];
        builder.enemyId = id; builder.builderIndex = index;
        builder.sp = state.BuilderSp; builder.spMax = state.BuilderSpMax; builder.state = state.BuilderState;
        builder.matter = state.Matter; builder.maxMatter = state.MatterMax;
        builder.energy = state.Energy; builder.maxEnergy = state.EnergyMax;
        var changed = pattern.instId != id || pattern.instBuilderId != builder.id;
        pattern.instId = id; pattern.instBuilderId = builder.id;
        if (changed) builderCreated(hive)?.Invoke(index, id);
        if (index == 1) hive.rootEnemyId = id;
        // Passive components supply native geometry/picking. Client non-unit logic stays disabled.
        if (pattern.isConn)
        {
            var connectorAdded = enemy.dfSConnectorId <= 0 || enemy.dfSConnectorId >= hive.connectors.cursor ||
                hive.connectors.buffer[enemy.dfSConnectorId].enemyId != id;
            if (connectorAdded) enemy.dfSConnectorId = hive.connectors.Add().id;
            ref var connector = ref hive.connectors.buffer[enemy.dfSConnectorId];
            connector.enemyId = id; connector.builderId = builder.id; connector.length = pattern.length; connector.size = pattern.size;
            if (connectorAdded && enemy.modelId > 0) RefreshConnectorColliders(sector, ref enemy, pattern.length);
        }
        if (pattern.protoId == 8109 && hive.antSegments != null)
            for (var segment = pattern.antSegOffset; segment < pattern.antSegOffset + (int)(pattern.length / 25f + 1f); segment++)
                hive.antSegments[segment] = builder.state >= 4 ? (byte)(segment % 255 + 1) : (byte)0;
    }

    private static void RefreshConnectorColliders(SpaceSector sector, ref EnemyData enemy, float length)
    {
        var prefab = SpaceSector.PrefabDescByModelIndex[enemy.modelIndex];
        if (sector.physics == null || prefab == null) return;
        if (enemy.colliderId != 0) sector.physics.RemoveLinkedColliderData(enemy.colliderId);
        enemy.colliderId = 0;
        AddConnectorColliders(sector, ref enemy, prefab.spacePhysicsColliders, length);
        AddConnectorColliders(sector, ref enemy, prefab.spaceBuildColliders, length);
    }

    private static void AddConnectorColliders(SpaceSector sector, ref EnemyData enemy, ColliderDataLF[] colliders, float length)
    {
        if (colliders == null) return;
        foreach (var template in colliders)
        {
            var collider = template;
            collider.ext.z = length / 2f; collider.pos.z = length / 2f;
            enemy.colliderId = sector.physics.AddColliderData(collider.BindToObject(enemy.id, enemy.colliderId,
                EObjectType.Enemy, enemy.astroId, enemy.pos, enemy.rot));
        }
    }

    internal static void Bind(SpaceSector sector, EnemyDFHiveSystem hive, int id, in SpaceEnemyState state)
    {
        ref var enemy = ref sector.enemyPool[id];
        if (state.Kind == SpaceEnemyKind.SpaceRelay)
        {
            DFRelayComponent relay = null;
            if (enemy.dfRelayId > 0 && enemy.dfRelayId < hive.relays.cursor)
                relay = hive.relays.buffer[enemy.dfRelayId];
            if (relay?.enemyId != id)
            {
                relay = hive.relays.Add(); relay.Init(hive); relay.enemyId = id;
                enemy.dfRelayId = relay.id;
            }
            relay.stage = state.Activity.Stage; relay.direction = state.Activity.Direction;
            relay.targetAstroId = state.Activity.TargetAstroId;
            relay.baseId = state.Activity.BaseId; relay.baseState = state.Activity.BaseState;
            if (hive.relayDocks?.Length > 0) relay.SetDockIndex(state.DockIndex);
        }
        else if (state.Kind == SpaceEnemyKind.SpaceTinder)
        {
            if (enemy.dfTinderId <= 0 || enemy.dfTinderId >= hive.tinders.cursor ||
                hive.tinders.buffer[enemy.dfTinderId].enemyId != id)
            {
                ref var added = ref hive.tinders.Add(); added.enemyId = id;
                enemy.dfTinderId = added.id;
            }
            ref var tinder = ref hive.tinders.buffer[enemy.dfTinderId];
            tinder.originHiveAstroId = state.OriginAstroId;
            // Native starmap tooltips dereference this target without a null guard. Its summary
            // can arrive after the traveling seed's state; HiveReady fills it in when available.
            tinder.targetHiveAstroId = sector.GetHiveByAstroId(state.Activity.TargetAstroId) != null ? state.Activity.TargetAstroId : 0;
            tinder.stage = state.Activity.Stage; tinder.direction = state.Activity.Direction;
            if (hive.tinderDocks?.Length > 0) tinder.SetDockIndex(hive, state.DockIndex);
        }
    }

    internal static void Forget(SpaceSector sector, int id)
    {
        var entries = For(sector);
        entries.Mobile.Remove(id); entries.Buildings.Remove(id); entries.Travelers.Remove(id);
        ref var enemy = ref sector.enemyPool[id];
        var hive = sector.GetHiveByAstroId(enemy.originAstroId);
        // The Base stream owns its links. Native cleanup must not mutate another stream's facts
        // or decrement a transit counter that this passive component never incremented.
        if (hive == null) return;
        if (enemy.builderId > 0 && enemy.builderId < hive.builders.cursor && hive.builders.buffer[enemy.builderId].enemyId == id)
        {
            var index = hive.builders.buffer[enemy.builderId].builderIndex;
            var patterns = HivePatternStorage.Read(hive);
            if (index > 0 && index < patterns.Length && patterns[index].instId == id)
            {
                patterns[index].instId = patterns[index].instBuilderId = 0;
                builderRemoved(hive)?.Invoke(index);
                var pattern = patterns[index];
                if (pattern.protoId == 8109 && hive.antSegments != null)
                    for (var segment = pattern.antSegOffset; segment < pattern.antSegOffset + (int)(pattern.length / 25f + 1f); segment++) hive.antSegments[segment] = 0;
            }
            hive.builders.Remove(enemy.builderId); enemy.builderId = 0;
        }
        if (enemy.dfSConnectorId > 0 && enemy.dfSConnectorId < hive.connectors.cursor && hive.connectors.buffer[enemy.dfSConnectorId].enemyId == id)
        { hive.connectors.Remove(enemy.dfSConnectorId); enemy.dfSConnectorId = 0; }
        if (enemy.dfRelayId > 0 && enemy.dfRelayId < hive.relays.cursor && hive.relays.buffer[enemy.dfRelayId] is { } relay) relay.baseId = 0;
        if (enemy.dfTinderId > 0 && enemy.dfTinderId < hive.tinders.cursor) hive.tinders.buffer[enemy.dfTinderId].targetHiveAstroId = 0;
    }

    internal static void PrepareDraw(SectorModel model, bool starmap)
    {
        var sector = GameMain.spaceSector;
        if (sector == null || !worlds.TryGetValue(sector, out var entries) || !ReferenceEquals(entries.Pool, sector.enemyPool)) return;
        foreach (var id in entries.Travelers) SpaceEnemyReplicaPools.RefreshPose(sector, id, starmap);
        // Vanilla's regular draw uploads these models; its starmap draw does not.
        if (starmap && entries.Travelers.Count > 0) model.gpuiManager?.objectRenderers[291]?.SyncInstBuffer();
    }

    internal static void RenderPose(VectorLF3 position, Quaternion rotation, bool starmap,
        VectorLF3 relativePos, Quaternion relativeRot, VectorLF3 starmapPos, out VectorLF3 result, out Quaternion rot)
    {
        if (starmap) { result = (position - starmapPos) * 0.00025; rot = rotation; }
        else { result = Maths.QInvRotateLF(relativeRot, position - relativePos); rot = Maths.QInvMultiply(relativeRot, rotation); }
    }
}
