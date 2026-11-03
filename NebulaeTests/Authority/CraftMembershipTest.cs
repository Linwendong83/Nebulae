using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeWorld.Authority.Adapters;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class CraftMembershipTest
{
    private static readonly AuthorityEpoch Epoch = new(21, 33);
    private static readonly ScopeKey Ground = new(PoolKind.GroundCraft, 101);
    private static PlanetFactory Factory(int size)
    {
        var factory = Uninitialized<PlanetFactory>();
        AccessTools.PropertySetter(typeof(PlanetFactory), nameof(PlanetFactory.planet)).Invoke(factory, [new PlanetData { id = 101 }]);
        factory.craftPool = new CraftData[size + 1]; factory.craftCursor = size + 1;
        return factory;
    }

    [TestMethod]
    public void ConstructionDronesAreExcludedFromMembershipAndIncrementalCandidates()
    {
        var factory = Factory(4); long tick = 0;
        for (var id = 1; id <= 3; id++) factory.craftPool[id] = new CraftData { id = id, prototype = ECraftProto.ConstructionDrone };
        factory.craftPool[4] = new CraftData { id = 4, prototype = (ECraftProto)7 };
        var source = new CraftSnapshotAdapter(Epoch, _ => factory, () => null!, () => tick);
        var candidates = new List<ObjectKey>(); var removed = new List<ObjectKey>();
        for (tick = 0; tick < 600; tick++)
        {
            candidates.Clear(); removed.Clear();
            if (!source.TryReadChanges(Ground, tick, candidates, removed)) TestAssert.IsTrue(source.TryReadMembers(Ground, candidates));
            TestAssert.IsLessThanOrEqualTo(1, candidates.Count);
            if (candidates.Count > 0) TestAssert.AreEqual(4, candidates[0].NativeId);
            TestAssert.IsEmpty(removed);
        }
    }

    [TestMethod]
    public void RemovalAndReuseWithinOneFrameRemainDifferentIdentities()
    {
        var factory = Factory(2); factory.craftPool[1] = new CraftData { id = 1, prototype = (ECraftProto)7 };
        var source = new CraftSnapshotAdapter(Epoch, _ => factory, () => null!, () => 0);
        var candidates = new List<ObjectKey>(); var removed = new List<ObjectKey>();
        source.TryReadMembers(Ground, candidates); var old = candidates.Single();
        factory.craftPool[1].id = 0; source.Removed(factory, 1);
        factory.craftPool[1] = new CraftData { id = 1, prototype = (ECraftProto)7 }; source.Added(factory, 1);
        candidates.Clear(); TestAssert.IsTrue(source.TryReadChanges(Ground, 1, candidates, removed));
        TestAssert.AreEqual(old.Generation + 1, candidates.Single().Generation); TestAssert.AreEqual(old, removed.Single());
        TestAssert.IsFalse(source.TryReadState(old, out _));
        removed.Clear(); candidates.Clear(); source.TryReadChanges(Ground, 2, candidates, removed);
        TestAssert.IsEmpty(removed); factory.craftPool[2] = new CraftData { id = 2, prototype = ECraftProto.ConstructionDrone }; source.Added(factory, 2);
        candidates.Clear(); source.TryReadChanges(Ground, 3, candidates, removed); TestAssert.HasCount(1, candidates);
    }

    [TestMethod]
    public void SpacePoolReuseAndUnreportedChangesConvergeAtTheNativeAudit()
    {
        var sector = Uninitialized<SpaceSector>();
        sector.craftPool = new CraftData[3]; sector.craftCursor = 3;
        sector.craftPool[1] = new CraftData { id = 1, isSpace = true };
        long tick = 0; var readable = true;
        var source = new CraftSnapshotAdapter(Epoch, _ => null!, () => readable ? sector : null!, () => tick);
        var scope = new ScopeKey(PoolKind.SpaceCraft, AuthorityScope.Sector);
        var candidates = new List<ObjectKey>(); var removed = new List<ObjectKey>();
        source.TryReadMembers(scope, candidates); var old = candidates.Single();
        sector.craftPool[1].id = 0; source.Removed(sector, 1);
        sector.craftPool[1] = new CraftData { id = 1, isSpace = true }; source.Added(sector, 1);
        candidates.Clear(); source.TryReadChanges(scope, 1, candidates, removed);
        TestAssert.AreEqual(old.Generation + 1, candidates.Single().Generation); TestAssert.AreEqual(old, removed.Single());
        sector.craftPool[2] = new CraftData { id = 2, isSpace = true }; // Another mod did not report its birth.
        tick = 1200; candidates.Clear(); removed.Clear();
        TestAssert.IsFalse(source.TryReadChanges(scope, tick, candidates, removed));
        TestAssert.IsTrue(source.TryReadMembers(scope, candidates)); TestAssert.HasCount(2, candidates);
        readable = false; candidates.Clear(); TestAssert.IsFalse(source.TryReadChanges(scope, 121, candidates, removed));
        TestAssert.IsFalse(source.TryReadMembers(scope, candidates)); TestAssert.IsEmpty(candidates);
    }
}
