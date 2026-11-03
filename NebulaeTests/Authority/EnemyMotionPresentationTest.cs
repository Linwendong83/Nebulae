using HarmonyLib;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld.Combat;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class EnemyMotionPresentationTest
{
    private static Quaternion Yaw(double degrees)
    {
        var angle = degrees * Math.PI / 360;
        return new Quaternion(0, (float)Math.Sin(angle), 0, (float)Math.Cos(angle));
    }

    private static void RotationIs(Quaternion expected, Quaternion actual)
    {
        var dot = Math.Abs((double)expected.x * actual.x + (double)expected.y * actual.y +
                           (double)expected.z * actual.z + (double)expected.w * actual.w);
        TestAssert.AreEqual(1d, dot, 1e-6);
    }

    private sealed class Rig : IDisposable
    {
        internal readonly object World = new();
        internal readonly EnemyMotionPresentation View = new();
        internal EnemyData[] Pool = new EnemyData[8];
        internal Rig()
        {
            Pool[3] = new EnemyData
            {
                id = 3,
                unitId = 9,
                protoId = 8128,
                modelIndex = 0,
                modelId = 1,
                astroId = 101,
                originAstroId = 101,
                dynamic = true,
                pos = new VectorLF3(0, 200, 0),
                rot = Quaternion.identity
            };
            TestAssert.IsFalse(View.Capture(World, Pool, 3, true, 100).Valid, "The first sample establishes the pool without a correction.");
        }

        internal void Checkpoint(double x, double tick = 100, bool space = false, double yaw = 0)
        {
            var before = View.Capture(World, Pool, 3, true, tick);
            Pool[3].pos.x = x;
            Pool[3].rot = Yaw(yaw);
            View.Rebase(World, Pool, 3, before, space);
        }

        internal VectorLF3 Position(double tick)
        {
            View.Sample(World, Pool, 3, tick, out var position, out _);
            return position;
        }

        public void Dispose() => View.Dispose();
    }

    [TestMethod]
    [DataRow(false, 6d)]
    [DataRow(true, 12.5d)]
    public void CheckpointIsImmediateInSimulationAndContinuousInPresentation(bool space, double distance)
    {
        using var rig = new Rig();
        rig.Pool[3].vel = new Vector3(6, 0, 0);
        rig.Checkpoint(distance, space: space, yaw: 60);
        TestAssert.AreEqual(distance, rig.Pool[3].pos.x);
        TestAssert.AreEqual(6f, rig.Pool[3].vel.x);
        TestAssert.AreEqual(0d, rig.Position(100).x, 1e-9);
        rig.View.Sample(rig.World, rig.Pool, 3, 100, out _, out var rotation);
        RotationIs(Quaternion.identity, rotation);
        rig.Pool[3].pos.x += 2; // Native movement continues independently of the offset.
        TestAssert.AreEqual(2 + distance / 2, rig.Position(103).x, 1e-9);
        TestAssert.AreEqual(2 + distance, rig.Position(106).x, 1e-9);
        TestAssert.AreEqual(rig.Pool[3].pos, rig.Position(120));
    }

    [TestMethod]
    public void ConsecutiveCheckpointsRebaseTheRemainingOffsetWithoutAJump()
    {
        using var rig = new Rig();
        rig.Checkpoint(6);
        var shown = rig.Position(102.5);
        rig.Checkpoint(7, 102.5);
        TestAssert.AreEqual(shown.x, rig.Position(102.5).x, 1e-9);
        TestAssert.AreEqual((shown.x + 7) / 2, rig.Position(105.5).x, 1e-9);
        TestAssert.AreEqual(7d, rig.Position(108.5).x, 1e-9);
    }

    [TestMethod]
    public void RotationCrossingZeroUsesTheShortestArcAndIgnoresQuaternionSign()
    {
        using var rig = new Rig();
        rig.Pool[3].rot = Yaw(350);
        rig.Checkpoint(0, yaw: 10);
        rig.View.Sample(rig.World, rig.Pool, 3, 103, out _, out var midpoint);
        RotationIs(Yaw(0), midpoint);
        var before = rig.View.Capture(rig.World, rig.Pool, 3, true, 103);
        var q = rig.Pool[3].rot;
        rig.Pool[3].rot = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        rig.View.Rebase(rig.World, rig.Pool, 3, before, false);
        rig.View.Sample(rig.World, rig.Pool, 3, 103, out _, out var after);
        RotationIs(midpoint, after);
    }

    [TestMethod]
    [DataRow(false, 8.01, 0d)]
    [DataRow(true, 200.01, 0d)]
    [DataRow(false, 1d, 121d)]
    public void LargeCorrectionsSnapInsteadOfDraggingTheModel(bool space, double distance, double yaw)
    {
        using var rig = new Rig();
        rig.Checkpoint(distance, space: space, yaw: yaw);
        TestAssert.AreEqual(rig.Pool[3].pos, rig.Position(100));
        rig.View.Sample(rig.World, rig.Pool, 3, 100, out _, out var rotation);
        RotationIs(rig.Pool[3].rot, rotation);
    }

    [TestMethod]
    [DataRow("astro")]
    [DataRow("origin")]
    [DataRow("unit")]
    [DataRow("proto")]
    public void IdentityAndCoordinateFrameChangesDiscardThePreviousOffset(string change)
    {
        using var rig = new Rig();
        rig.Checkpoint(6);
        var before = rig.View.Capture(rig.World, rig.Pool, 3, true, 101);
        switch (change)
        {
            case "astro": rig.Pool[3].astroId++; break;
            case "origin": rig.Pool[3].originAstroId++; break;
            case "unit": rig.Pool[3].unitId++; break;
            case "proto": rig.Pool[3].protoId++; break;
        }
        rig.Pool[3].pos.x = 7;
        rig.View.Rebase(rig.World, rig.Pool, 3, before, false);
        TestAssert.AreEqual(7d, rig.Position(101).x);
    }

    [TestMethod]
    public void RemovalResyncPoolReplacementAndSessionDisposalCannotReuseOffsets()
    {
        using var rig = new Rig();
        rig.Checkpoint(6);
        rig.View.Remove(rig.World, 3);
        TestAssert.AreEqual(6d, rig.Position(100).x);
        rig.Checkpoint(7, 101);
        rig.View.Capture(rig.World, rig.Pool, 3, false, 102);
        TestAssert.AreEqual(7d, rig.Position(102).x);
        rig.Checkpoint(6, 103);
        rig.Pool = (EnemyData[])rig.Pool.Clone();
        TestAssert.IsFalse(rig.View.Capture(rig.World, rig.Pool, 3, true, 104).Valid);
        TestAssert.AreEqual(6d, rig.Position(104).x);
        rig.Checkpoint(7, 105);
        rig.View.Forget(rig.World);
        TestAssert.AreEqual(7d, rig.Position(105).x);
        rig.View.Capture(rig.World, rig.Pool, 3, true, 106);
        rig.Checkpoint(6, 107);
        rig.View.Dispose();
        TestAssert.AreEqual(6d, rig.Position(107).x);
    }

    [TestMethod]
    public void PauseFreezesTheFractionalClockAndResumePreservesContinuity()
    {
        using var rig = new Rig();
        var clock = new BattleVisualClock();
        clock.Sample(100, 10, 60);
        rig.Checkpoint(6, clock.Sample(100, 10, 60));
        var before = rig.Position(clock.Sample(100, 10.005, 60));
        var paused = rig.Position(clock.Sample(100, 20, 60, true));
        TestAssert.AreEqual(before.x, paused.x, 1e-9);
        TestAssert.AreEqual(paused.x, rig.Position(clock.Sample(100, 40, 60, true)).x, 1e-9);
        TestAssert.AreEqual(6d, rig.Position(clock.Sample(106, 40.1, 60)).x, 1e-9);
    }

    [TestMethod]
    public void RepeatedCheckpointsAndRecycledSlotsRetainOnlyLiveUnitState()
    {
        using var rig = new Rig();
        for (var i = 0; i < 10000; i++) rig.Checkpoint(i % 2, 100 + i * 6);
        TestAssert.AreEqual(1, RetainedEntries(rig));
        for (var i = 0; i < 1000; i++)
        {
            rig.View.Remove(rig.World, 3);
            rig.Pool[3].unitId++;
            rig.Checkpoint(i % 2, 60100 + i * 6);
            TestAssert.AreEqual(1, RetainedEntries(rig));
        }
        rig.View.Remove(rig.World, 3);
        TestAssert.AreEqual(0, RetainedEntries(rig));
        rig.View.Forget(rig.World);
        TestAssert.AreEqual(0, RetainedEntries(rig));
    }

    private static int RetainedEntries(Rig rig)
    {
        var table = AccessTools.Field(typeof(EnemyMotionPresentation), "worlds").GetValue(rig.View);
        var arguments = new object?[] { rig.World, null };
        if (!(bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, arguments)) return 0;
        var world = arguments[1];
        TestAssert.IsNotNull(world);
        var entries = (System.Collections.IDictionary)AccessTools.Field(world.GetType(), "Entries").GetValue(world);
        return entries.Count;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DrawScopeRestoresCpuInstancesOnExceptionAndRequiresTheFinalRawUpload(bool space)
    {
        using var rig = new Rig();
        rig.Checkpoint(6);
        var factoryModel = Uninitialized<FactoryModel>();
        var sectorModel = Uninitialized<SectorModel>();
        var renderer = Uninitialized<DynamicRenderer>();
        renderer.instPool = new GPUOBJECT[2];
        renderer.instPool[1] = new GPUOBJECT { objId = 3, posx = 6, posy = 200, rotw = 1 };
        factoryModel.gpuiManager = Uninitialized<GPUInstancingManager>();
        factoryModel.gpuiManager.objectRenderers = [renderer];
        // Native hive unit loops accept ordinary SpaceObjectRenderer instances too.
        var spaceRenderer = Uninitialized<SpaceObjectRenderer>();
        spaceRenderer.instPool = new SPACEOBJECT[2];
        spaceRenderer.instPool[1] = new SPACEOBJECT { objId = 3, astroId = 101, posx = 6, posy = 200, rotw = 1 };
        sectorModel.gpuiManager = Uninitialized<SSGPUInstancingManager>();
        sectorModel.gpuiManager.objectRenderers = [spaceRenderer];
        var originalGround = renderer.instPool[1];
        var originalSpace = spaceRenderer.instPool[1];
        EnemyMotionPresentation.DrawState? draw = null;
        try
        {
            draw = rig.View.BeginDraw(rig.World, rig.Pool, space ? null : factoryModel, space ? sectorModel : null, 103);
            TestAssert.IsNotNull(draw);
            TestAssert.AreEqual(3f, space ? spaceRenderer.instPool[1].posx : renderer.instPool[1].posx);
            TestAssert.AreEqual(6d, rig.Pool[3].pos.x, "Rendering must not write the logical enemy.");
            // Stand in for the game's buffer upload, which consumes the dirty flag.
            factoryModel.enemyUnitsDirty = sectorModel.enemyUnitsDirty = false;
            throw new InvalidOperationException("Drawing failed");
        }
        catch (InvalidOperationException) { }
        finally
        {
            if (space) SpaceEnemyMotionPresentation_Patch.Finalizer(draw);
            else GroundEnemyMotionPresentation_Patch.Finalizer(draw);
        }
        TestAssert.AreEqual(originalGround, renderer.instPool[1]);
        TestAssert.AreEqual(originalSpace, spaceRenderer.instPool[1]);
        TestAssert.IsTrue(space ? sectorModel.enemyUnitsDirty : factoryModel.enemyUnitsDirty);
        var second = rig.View.BeginDraw(rig.World, rig.Pool, space ? null : factoryModel, space ? sectorModel : null, 103);
        TestAssert.AreEqual(3f, space ? spaceRenderer.instPool[1].posx : renderer.instPool[1].posx, "Another camera cannot compound the offset.");
        second.Restore();
        rig.View.BeginDraw(rig.World, rig.Pool, space ? null : factoryModel, space ? sectorModel : null, 106)?.Restore();
        TestAssert.AreEqual(6f, space ? spaceRenderer.instPool[1].posx : renderer.instPool[1].posx);
    }

}
