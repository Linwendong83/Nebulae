using System.Collections.Generic;
using System;
using NebulaeModel.DataStructures;
using UnityEngine;

namespace NebulaeWorld.Planet;

/// <summary>Client display construction, preserving the supported game's stage and object ordering.</summary>
internal sealed class FactoryModelLoader
{
    private sealed class Progress
    {
        public int Cursor = 1, FinalStep, Mesh;
        public bool TerrainPrepared;
    }
    private readonly Dictionary<int, Progress> progress = new();
    private readonly PlanetManager manager;
    private readonly Action<PlanetData, int, int> buildObject;
    public FactoryModelLoader(PlanetManager manager, Action<PlanetData, int, int> buildObject = null)
    { this.manager = manager; this.buildObject = buildObject ?? BuildObject; }
    public void Cancel(int planet) => progress.Remove(planet);
    public void Clear() => progress.Clear();

    public void Step(PlanetData planet, WorkBudget budget)
    {
        var stage = PlanetManager.LoadingStage;
        if (stage > PlanetModelingManager.Debug_LoadingPlanetFactoryMain_MaxStage) return;
        if (!progress.TryGetValue(planet.id, out var work)) progress.Add(planet.id, work = new Progress());
        var factory = planet.factory;
        while (stage < 8)
        {
            var end = stage switch
            {
                1 => factory.vegeCursor,
                2 => factory.veinCursor,
                3 => factory.prebuildCursor,
                4 => factory.entityCursor,
                5 => factory.craftCursor,
                6 => factory.enemyCursor,
                7 => factory.ruinCursor,
                _ => 1
            };
            while (work.Cursor < end)
            {
                buildObject(planet, stage, work.Cursor++);
                if (budget.Expired && work.Cursor < end) return;
            }
            planet.NotifyFactingStageComplete(stage);
            PlanetManager.LoadingStage = stage + 1;
            work.Cursor = 1;
            // Stage notifications only publish the completed stage; they do not
            // require a new Unity frame. Share the original budget across stages.
            if (budget.Expired) return;
            stage++;
            if (stage > PlanetModelingManager.Debug_LoadingPlanetFactoryMain_MaxStage) return;
        }

        while (true)
        {
            switch (work.FinalStep)
            {
                case 0: factory.cargoTraffic.CreateRenderingBatches(); break;
                case 1:
                    planet.factoryModel.gpuiManager.SyncAllGPUBuffer();
                    break;
                case 2: planet.factoryModel.RefreshPowerNodes(); break;
                case 3:
                    planet.factoryModel.RefreshTurrets();
                    planet.NotifyFactingStageComplete(8);
                    break;
                case 4: factory.LocalizeEntities(); break;
                case 5:
                    if (!work.TerrainPrepared)
                    {
                        manager.ApplyTerrain(planet);
                        work.TerrainPrepared = true;
                    }
                    while (work.Mesh < planet.dirtyFlags.Length)
                    {
                        var mesh = work.Mesh++;
                        if (planet.dirtyFlags[mesh]) planet.UpdateDirtyMesh(mesh);
                        if (budget.Expired && work.Mesh < planet.dirtyFlags.Length) return;
                    }
                    break;
                default:
                    planet.NotifyFactoryLoaded();
                    PlanetManager.LoadingStage = 0;
                    PlanetManager.LoadingPlanet = null;
                    progress.Remove(planet.id);
                    return;
            }
            work.FinalStep++;
            if (budget.Expired) return;
        }
    }

    private static void BuildObject(PlanetData planet, int stage, int id)
    {
        var factory = planet.factory;
        var physics = planet.physics;
        var models = planet.factoryModel?.gpuiManager;
        switch (stage)
        {
            case 1:
                ref var vege = ref factory.vegePool[id];
                if (vege.id != id) return;
                var vegetation = PlanetModelingManager.vegeProtos[vege.protoId];
                if (vegetation == null) return;
                vege.modelId = vege.colliderId = 0;
                vege.modelId = models.AddModel(vege.modelIndex, id, vege.pos, vege.rot, false);
                if (vegetation.prefabDesc.colliders != null)
                    foreach (var collider in vegetation.prefabDesc.colliders)
                        vege.colliderId = physics.AddColliderData(collider.BindToObject(id, vege.colliderId, EObjectType.Vegetable, vege.pos, vege.rot, vege.scl));
                break;
            case 2:
                ref var vein = ref factory.veinPool[id];
                if (vein.id != id) return;
                if (vein.amount <= 0) { factory.RemoveVeinData(id); return; }
                var mineral = PlanetModelingManager.veinProtos[(uint)vein.type];
                if (mineral == null) return;
                vein.modelId = vein.colliderId = vein.minerBaseModelId = 0;
                vein.minerCircleModelId0 = vein.minerCircleModelId1 = vein.minerCircleModelId2 = vein.minerCircleModelId3 = 0;
                ref var animation = ref factory.veinAnimPool[id];
                animation.time = vein.amount >= 20000 ? 0f : 1f - vein.amount * 5E-05f;
                animation.prepare_length = 0; animation.working_length = 1; animation.state = (uint)vein.type; animation.power = 0;
                vein.modelId = models.AddModel(vein.modelIndex, id, vein.pos, Maths.SphericalRotation(vein.pos, UnityEngine.Random.value * 360f), false);
                if (mineral.prefabDesc.colliders != null)
                    foreach (var collider in mineral.prefabDesc.colliders)
                        vein.colliderId = physics.AddColliderData(collider.BindToObject(id, vein.colliderId, EObjectType.Vein,
                            vein.pos, Quaternion.FromToRotation(Vector3.up, vein.pos.normalized)));
                factory.RefreshVeinMiningDisplay(id, 0, 0);
                break;
            case 3:
                ref var prebuild = ref factory.prebuildPool[id];
                if (prebuild.id != id) return;
                var item = LDB.items.Select(prebuild.protoId);
                if (item == null) return;
                prebuild.modelId = prebuild.colliderId = 0;
                prebuild.modelId = models.AddPrebuildModel(prebuild.modelIndex, id, prebuild.pos, prebuild.rot, false);
                if (item.prefabDesc.colliders != null)
                    foreach (var collider in item.prefabDesc.colliders)
                        prebuild.colliderId = physics.AddColliderData(collider.BindToObject(id, prebuild.colliderId,
                            EObjectType.Prebuild, prebuild.pos, prebuild.rot));
                factory.PostRefreshPrebuildDisplay(id, true);
                break;
            case 4:
                ref var entity = ref factory.entityPool[id];
                if (entity.id != id) return;
                entity.modelId = entity.mmblockId = entity.colliderId = entity.audioId = 0;
                factory.CreateEntityDisplayComponents(id);
                break;
            case 5:
                ref var craft = ref factory.craftPool[id];
                if (craft.id != id) return;
                craft.modelId = craft.mmblockId = craft.colliderId = craft.audioId = 0;
                factory.CreateCraftDisplayComponents(id);
                break;
            case 6:
                ref var enemy = ref factory.enemyPool[id];
                if (enemy.id != id) return;
                enemy.modelId = enemy.mmblockId = enemy.colliderId = enemy.audioId = 0;
                factory.CreateEnemyDisplayComponents(id);
                break;
            case 7:
                ref var ruin = ref factory.ruinPool[id];
                if (ruin.id != id) return;
                ruin.modelId = ruin.mmblockId = ruin.colliderId = 0;
                factory.CreateRuinDisplayComponent(id);
                break;
        }
    }
}
