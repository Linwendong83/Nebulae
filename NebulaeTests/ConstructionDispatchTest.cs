using System.Reflection;
using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaePatcher.Patches.Transpilers;
using NebulaeModel.Packets.Factory;
using NebulaeWorld;
using NebulaeWorld.Factory;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class ConstructionDispatchTest
{
    [TestMethod]
    public void SupplyTransformationCoversBothNativeDebits()
    {
        var codes = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(ConstructionModuleComponent), "PlaceItems"));
        var result = ConstructionModuleComponent_Transpiler.PlaceItems_Transpiler(codes).ToArray();
        var reports = result.Count(c => c.operand is MethodInfo method && method.Name == "Report");
        var field = AccessTools.Field(typeof(PrebuildData), nameof(PrebuildData.itemRequired));
        var diagnostic = string.Join("\n", codes.Select((c, i) => (c, i)).Where(x => x.c.operand is FieldInfo f && f.Name == "itemRequired")
            .SelectMany(x => codes.Skip(Math.Max(0, x.i - 3)).Take(9)).Select(c => c.ToString()));
        TestAssert.AreEqual(2, reports, diagnostic);
    }

    [TestMethod]
    public void FlightOffersRejectDuplicateOverflowAndStaleIdentities()
    {
        TestAssert.IsTrue(ConstructionFlight.ValidTargets([-1, -2, -3, -4], [1, 2, 3, 4]));
        TestAssert.IsTrue(ConstructionFlight.ValidTargets([-1, 0, -3, 0], [1, 0, 3, 0]));
        TestAssert.IsFalse(ConstructionFlight.ValidTargets([-1, -1, 0, 0], [1, 1, 0, 0]));
        TestAssert.IsFalse(ConstructionFlight.ValidTargets([int.MinValue, 0, 0, 0], [1, 0, 0, 0]));
        TestAssert.IsFalse(ConstructionFlight.ValidTargets([-1, 0, 0, 0], [0, 0, 0, 0]));
        TestAssert.IsFalse(ConstructionFlight.ValidTargets([-1, 0, 0, 0], [1, 1, 0, 0]));
    }

    [TestMethod]
    public void OnlyTheAssignmentsTheSearchWroteAreResetBeforeTheNextLaunchPass()
    {
        var factory = Uninitialized<PlanetFactory>();
        SetBackingField(factory, "planet", new PlanetData { id = 101 });
        factory.prebuildCursor = 6; factory.prebuildPool = new PrebuildData[6];
        for (var id = 1; id < 6; id++) factory.prebuildPool[id].id = id;
        var module = Uninitialized<ConstructionModuleComponent>();
        module.buildTargets = new ConstructTarget[4];
        PrebuildAssignmentSweep.Reset();
        PrebuildAssignmentSweep.BeginFrame(factory, incremental: true); // Seeds the full sweep.
        factory.prebuildPool[2].builderModuleId = 7; factory.prebuildPool[2].builderValue = 2f;
        module.buildTargets[0].objectId = 2;
        PrebuildAssignmentSweep.Record(factory, module);
        // An assignment this peer never saw, for example one an imported snapshot restored.
        factory.prebuildPool[3].builderModuleId = 9; factory.prebuildPool[3].builderValue = 1f;
        factory.prebuildPool[4].builderModuleId = 5; factory.prebuildPool[4].builderValue = 1f;
        factory.prebuildPool[4].builderLaunched = true;
        PrebuildAssignmentSweep.BeginFrame(factory, incremental: true);
        TestAssert.AreEqual(0, factory.prebuildPool[2].builderModuleId, "The assignment the search wrote is reset.");
        TestAssert.AreEqual(0f, factory.prebuildPool[2].builderValue);
        TestAssert.AreEqual(9, factory.prebuildPool[3].builderModuleId, "An unobserved assignment survives an incremental frame.");
        TestAssert.AreEqual(5, factory.prebuildPool[4].builderModuleId, "Launched sites are never rewritten.");
        // A client does not run the native search, so it keeps the full sweep.
        PrebuildAssignmentSweep.BeginFrame(factory, incremental: false);
        TestAssert.AreEqual(0, factory.prebuildPool[3].builderModuleId);
        TestAssert.AreEqual(5, factory.prebuildPool[4].builderModuleId);
        PrebuildAssignmentSweep.Reset();
    }

    [TestMethod]
    public void ReadyTargetsRemainVisibleToMultipleBasesWithoutAnAssignment()
    {
        var previous = Multiplayer.Session;
        try
        {
            var session = Uninitialized<MultiplayerSession>();
            session.Server = Uninitialized<global::NebulaeNetwork.Server>(); Multiplayer.Session = session;
            var factory = Uninitialized<PlanetFactory>();
            SetBackingField(factory, "planet", new PlanetData { id = 101 });
            factory.prebuildCursor = 2; factory.prebuildPool = new PrebuildData[2]; factory.prebuildPool[1].id = 1;
            var dispatch = new BuildDispatchManager();
            var first = new ConstructionModuleComponent { entityId = 10, droneEnabled = true, droneConstructEnabled = true, droneCount = 3 };
            var second = new ConstructionModuleComponent { entityId = 20, droneEnabled = true, droneConstructEnabled = true, droneCount = 3 };
            TestAssert.IsTrue(dispatch.CanQueue(factory, first, 1));
            TestAssert.IsTrue(dispatch.CanQueue(factory, second, 1));
            TestAssert.IsFalse(dispatch.TryGet(101, 1, out _));
            factory.prebuildPool[1].builderLaunched = true;
            TestAssert.IsFalse(dispatch.CanQueue(factory, first, 1));
        }
        finally { Multiplayer.Session = previous; }
    }

    [TestMethod]
    public void GrantKeepsAllFourTargetsAcrossTheWire()
    {
        var packet = new ConstructionLaunchGrantPacket();
        BuildDispatchManager.Fill(packet, 2, 101, [-1, -2, -3, -4], [11, 12, 13, 14], 1);
        var received = RoundTrip(packet);
        TestAssert.AreEqual(-4, received.Next3ObjectId);
        TestAssert.AreEqual(14L, received.Next3Generation);
        TestAssert.AreEqual((ushort)2, received.PlayerId);
    }

    [TestMethod]
    public void FactoryUnloadingDoesNotReuseConnectionSequencesOrLoseSupplyReceipts()
    {
        var dispatch = new BuildDispatchManager();
        SetField(dispatch, "nextOffer", 41L); SetField(dispatch, "nextSupply", 27L);
        var pending = (Dictionary<long, PrebuildItemRequiredUpdate>)AccessTools.Field(typeof(BuildDispatchManager), "pendingSupply").GetValue(dispatch);
        pending[27] = new PrebuildItemRequiredUpdate(101, 1, 1) { Instance = 3 };
        dispatch.OnFactoriesUnloaded();
        TestAssert.AreEqual(41L, AccessTools.Field(typeof(BuildDispatchManager), "nextOffer").GetValue(dispatch));
        TestAssert.AreEqual(27L, AccessTools.Field(typeof(BuildDispatchManager), "nextSupply").GetValue(dispatch));
        TestAssert.IsTrue(dispatch.SupplyPending);
    }

    [TestMethod]
    public void NativeConstructionPatchesEmitValidExecutableMethods()
    {
        var harmony = new Harmony("nebulae-tests.construction-dispatch");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(ConstructionModuleComponent), "PlaceItems"),
                transpiler: new HarmonyMethod(typeof(ConstructionModuleComponent_Transpiler), "PlaceItems_Transpiler"));
            foreach (var name in new[] { "UpdateDrones", "ExecuteBuildTasks" })
                harmony.Patch(AccessTools.Method(typeof(ConstructionSystem), name),
                    transpiler: new HarmonyMethod(typeof(ConstructionSystem_Patch), "UpdateDrones_Transpiler"));
        }
        finally { harmony.UnpatchSelf(); }
    }
}
