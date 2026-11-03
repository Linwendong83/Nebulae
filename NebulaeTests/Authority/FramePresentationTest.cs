using HarmonyLib;
using NebulaeWorld.Authority.Adapters;
using NebulaeWorld.Combat;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class FramePresentationTest
{

    private sealed class Rig
    {
        internal readonly PlanetFactory Factory = Uninitialized<PlanetFactory>();
        internal readonly FactoryModel Model = Uninitialized<FactoryModel>();
        internal readonly DynamicRenderer Renderer = Uninitialized<DynamicRenderer>();
        internal readonly DynamicRenderer CraftRenderer = Uninitialized<DynamicRenderer>();

        internal Rig()
        {
            var planet = new PlanetData { id = 101, factoryModel = Model };
            AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(Factory, planet);
            Factory.enemyPool = new EnemyData[4];
            Factory.enemyPool[3] = new EnemyData
            {
                id = 3,
                unitId = 1,
                modelId = 1,
                pos = new VectorLF3(1, 200, 3),
                rot = Quaternion.identity
            };
            Factory.craftPool = new CraftData[4];
            Factory.craftPool[3] = new CraftData
            {
                id = 3,
                modelId = 1,
                modelIndex = 1,
                pos = new VectorLF3(1, 200, 3),
                rot = Quaternion.identity
            };
            Factory.enemySystem = Uninitialized<EnemyDFGroundSystem>();
            Factory.enemySystem.units = new DataPool<EnemyUnitComponent>();
            Factory.enemySystem.units.Reset();
            ref var unit = ref Factory.enemySystem.units.Add();
            unit.anim = 0.25f; unit.disturbValue = 0.5f; unit.steering = 0.75f; unit.speed = 1;
            Renderer.instPool = new GPUOBJECT[2];
            Renderer.extraPool = new Vector4[2];
            CraftRenderer.instPool = new GPUOBJECT[2];
            Model.gpuiManager = Uninitialized<GPUInstancingManager>();
            Model.gpuiManager.objectRenderers = [Renderer, CraftRenderer];
        }

        internal void Enemy() => GroundEnemyReplicaPools.RefreshPose(Factory, 3, false, false);
        internal void Craft() => CraftPresentation.RefreshGround(Factory, 3, false, false);
    }

    [TestMethod]
    public void UnchangedEnemyPoseAndAnimationDoNotRequestAnotherUpload()
    {
        var rig = new Rig();
        rig.Enemy();
        TestAssert.IsTrue(rig.Model.enemyUnitsDirty, "A new model must receive its initial pose and animation.");
        TestAssert.AreEqual(200f, rig.Renderer.instPool[1].posy);
        TestAssert.AreEqual(0.25f, rig.Renderer.extraPool[1].x);
        rig.Model.enemyUnitsDirty = false;
        for (var frame = 0; frame < 1000; frame++) rig.Enemy();
        TestAssert.IsFalse(rig.Model.enemyUnitsDirty, "Idle logic frames must not force an identical GPU upload.");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void AnimationChangesStillUploadWithoutPositionOrRotationChanges(int field)
    {
        var rig = new Rig();
        rig.Enemy();
        rig.Model.enemyUnitsDirty = false;
        ref var unit = ref rig.Factory.enemySystem.units.buffer[1];
        switch (field)
        {
            case 0: unit.anim += 0.125f; break;
            case 1: unit.disturbValue += 0.125f; break;
            case 2: unit.steering += 0.125f; break;
            case 3: unit.speed += 0.125f; break;
        }
        rig.Enemy();
        TestAssert.IsTrue(rig.Model.enemyUnitsDirty);
        TestAssert.AreEqual(new Vector4(unit.anim, unit.disturbValue, unit.steering, unit.speed), rig.Renderer.extraPool[1]);
        rig.Model.enemyUnitsDirty = false;
        rig.Enemy();
        TestAssert.IsFalse(rig.Model.enemyUnitsDirty);
    }

    [TestMethod]
    public void ARebuiltEnemyModelIsRepairedEvenWhenSimulationHasNotMoved()
    {
        var rig = new Rig();
        rig.Enemy();
        rig.Renderer.instPool = new GPUOBJECT[2];
        rig.Renderer.extraPool = new Vector4[2];
        rig.Model.enemyUnitsDirty = false;
        rig.Enemy();
        TestAssert.IsTrue(rig.Model.enemyUnitsDirty);
        TestAssert.AreEqual(200f, rig.Renderer.instPool[1].posy);
        TestAssert.AreEqual(1f, rig.Renderer.instPool[1].rotw);
        TestAssert.AreEqual(0.25f, rig.Renderer.extraPool[1].x);
    }

    [TestMethod]
    public void GroundCraftUploadsOnlyWhenItsRenderedPoseChanges()
    {
        var rig = new Rig();
        rig.Craft();
        TestAssert.IsTrue(rig.Model.craftDirty);
        rig.Model.craftDirty = false;
        for (var frame = 0; frame < 1000; frame++) rig.Craft();
        TestAssert.IsFalse(rig.Model.craftDirty);
        rig.Factory.craftPool[3].rot = new Quaternion(0, 1, 0, 0);
        rig.Craft();
        TestAssert.IsTrue(rig.Model.craftDirty);
        TestAssert.AreEqual(1f, rig.CraftRenderer.instPool[1].roty);
        rig.CraftRenderer.instPool = new GPUOBJECT[2];
        rig.Model.craftDirty = false;
        rig.Craft();
        TestAssert.IsTrue(rig.Model.craftDirty, "A new render pool must receive the stationary craft's pose.");
        TestAssert.AreEqual(200f, rig.CraftRenderer.instPool[1].posy);
    }

    [TestMethod]
    public void UnchangedObjectsCannotClearAnUploadRequestedByAnotherObject()
    {
        var rig = new Rig();
        rig.Enemy(); rig.Craft();
        rig.Enemy(); rig.Craft();
        TestAssert.IsTrue(rig.Model.enemyUnitsDirty);
        TestAssert.IsTrue(rig.Model.craftDirty);
    }
}
