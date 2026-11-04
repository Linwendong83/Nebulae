using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority.Adapters;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class TurretSnapshotAdapterTest
{
    [TestMethod]
    public void NativeProgressReusesEncodedStateUntilAnExternalTransactionOrRetirement()
    {
        var previousData = GameMain.data;
        try
        {
            var planet = new PlanetData { id = 101 };
            var factory = Uninitialized<PlanetFactory>();
            AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, planet);
            planet.factory = factory;
            factory.entityCursor = 2;
            factory.entityPool = new EntityData[2];
            factory.entityPool[1] = new EntityData { id = 1, modelIndex = 400, turretId = 1 };
            factory.defenseSystem = Uninitialized<DefenseSystem>();
            factory.defenseSystem.turrets = new DataPool<TurretComponent>();
            factory.defenseSystem.turrets.Reset();
            ref var turret = ref factory.defenseSystem.turrets.Add();
            turret.entityId = 1;
            turret.type = ETurretType.Gauss;
            turret.itemId = 1601;
            turret.itemCount = 5;
            var data = Uninitialized<GameData>();
            data.galaxy = new GalaxyData { stars = new[] { new StarData { planets = new[] { planet } } } };
            GameMain.data = data;
            var scope = new ScopeKey(PoolKind.Turret, 101);
            var adapter = new TurretSnapshotAdapter(new AuthorityEpoch(11, 22));
            var members = new List<ObjectKey>();
            TestAssert.IsTrue(adapter.TryReadMembers(scope, members));
            var original = members.Single();
            TestAssert.IsTrue(adapter.TryReadState(original, out var encoded));

            // No animation or sign pools exist. Capturing the server's decisions must not read them.
            for (var i = 0; i < 1000; i++)
            {
                turret.localDir = new Vector3(i * 0.001f, 1, 0);
                turret.aimt = i;
                turret.roundFire = i;
                TestAssert.IsTrue(adapter.TryReadState(original, out var same));
                TestAssert.AreSame(encoded, same, "Continuous presentation must not allocate a fresh encoded record.");
            }
            turret.itemCount = 4;
            TestAssert.IsTrue(adapter.TryReadState(original, out var predicted));
            TestAssert.AreSame(encoded, predicted);
            adapter.Invalidate(original);
            TestAssert.IsTrue(adapter.TryReadState(original, out var changed));
            TestAssert.AreNotSame(encoded, changed);
            TestAssert.IsTrue(TurretStateCodec.TryDecode(changed, out var decoded));
            TestAssert.AreEqual((short)4, decoded.ItemCount);

            // A remove/rebuild between scans must invalidate both the identity and cached payload.
            adapter.Retire(101, 1);
            TestAssert.IsFalse(adapter.TryReadState(original, out _));
            members.Clear();
            adapter.TryReadMembers(scope, members);
            TestAssert.IsGreaterThan(original.Generation, members.Single().Generation);
            TestAssert.IsTrue(adapter.TryReadState(members.Single(), out var rebuilt));
            TestAssert.AreNotSame(changed, rebuilt);
        }
        finally { GameMain.data = previousData; }
    }

    [TestMethod]
    public void ThousandsOfTurretsUseBoundedSlicesAndPreserveSameFrameLifecycleChanges()
    {
        var previousData = GameMain.data;
        try
        {
            const int count = 2400;
            var factory = Uninitialized<PlanetFactory>();
            var planet = new PlanetData { id = 101, factory = factory };
            AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, planet);
            factory.entityCursor = count + 1;
            factory.entityPool = new EntityData[count + 1];
            factory.defenseSystem = Uninitialized<DefenseSystem>();
            var pool = factory.defenseSystem.turrets = new DataPool<TurretComponent>(); pool.Reset();
            for (var id = 1; id <= count; id++)
            {
                ref var turret = ref pool.Add(); turret.entityId = id; turret.type = ETurretType.Gauss;
                factory.entityPool[id] = new EntityData { id = id, modelIndex = 400, turretId = turret.id };
            }
            var data = Uninitialized<GameData>();
            data.galaxy = new GalaxyData { stars = new[] { new StarData { planets = new[] { planet } } } };
            GameMain.data = data;
            var adapter = new TurretSnapshotAdapter(new AuthorityEpoch(11, 22));
            var scope = new ScopeKey(PoolKind.Turret, 101);
            var members = new List<ObjectKey>(); adapter.TryReadMembers(scope, members);
            var key = members[1];
            var candidates = new List<ObjectKey>(); var removed = new List<ObjectKey>();
            adapter.Invalidate(key);
            TestAssert.IsTrue(adapter.TryReadChanges(scope, GameMain.gameTick + 1, candidates, removed));
            TestAssert.Contains(key, candidates);
            TestAssert.IsLessThanOrEqualTo(21, candidates.Count);
            adapter.StateSampled(key);
            adapter.Retire(101, 2); pool.Remove(2);
            candidates.Clear(); removed.Clear();
            TestAssert.IsTrue(adapter.TryReadChanges(scope, GameMain.gameTick + 2, candidates, removed));
            TestAssert.AreEqual(key, removed.Single());
            ref var rebuilt = ref pool.Add(); rebuilt.entityId = 2; rebuilt.type = ETurretType.Gauss;
            factory.entityPool[2].turretId = rebuilt.id;
            adapter.Added(factory, 2);
            candidates.Clear(); removed.Clear();
            TestAssert.IsTrue(adapter.TryReadChanges(scope, GameMain.gameTick + 3, candidates, removed));
            var replacement = candidates.Single(x => x.NativeId == 2);
            TestAssert.AreEqual(key.Generation + 1, replacement.Generation);
            TestAssert.IsFalse(adapter.TryReadState(key, out _));
            adapter.TryReadState(replacement, out var encoded);
            rebuilt.itemCount = 9;
            adapter.TryReadSnapshotState(replacement, out var current);
            TestAssert.AreNotSame(encoded, current);
            TestAssert.IsTrue(TurretStateCodec.TryDecode(current, out var decoded));
            TestAssert.AreEqual((short)9, decoded.ItemCount);
        }
        finally { GameMain.data = previousData; }
    }
}
