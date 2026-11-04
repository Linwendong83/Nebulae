using HarmonyLib;
using global::NebulaeModel.Authority;
using global::NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority.Adapters;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class RuinReplicationTest
{
    [TestMethod]
    public void PitStatePreservesGeothermalLevelButExcludesHostComponentHandles()
    {
        var source = new RuinData
        {
            id = 7,
            modelIndex = 406,
            lifeTime = -18,
            pos = new Vector3(1, 200, 2),
            rot = Quaternion.identity,
            modelId = 91,
            colliderId = 92,
            hashAddress = 93,
            mmblockId = 94
        };
        var bytes = RuinStateCodec.Encode(source);
        var result = RuinStateCodec.Decode(bytes);
        TestAssert.AreEqual(-18, result.lifeTime); TestAssert.AreEqual(source.pos, result.pos);
        TestAssert.AreEqual(0, result.modelId + result.colliderId + result.hashAddress + result.mmblockId);
        for (var count = 0; count < bytes.Length; count++)
            TestAssert.Throws<InvalidDataException>(() => RuinStateCodec.Decode(bytes.Take(count).ToArray()));
        TestAssert.Throws<InvalidDataException>(() => RuinStateCodec.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
        source.pos.x = float.NaN;
        TestAssert.Throws<InvalidDataException>(() => RuinStateCodec.Decode(RuinStateCodec.Encode(source)));
    }

    [TestMethod]
    public void SameFramePitRemovalAndReuseChangesTheAuthorityIdentity()
    {
        var previous = GameMain.data;
        try
        {
            var planet = new PlanetData { id = 101 };
            var factory = Uninitialized<PlanetFactory>(); planet.factory = factory;
            AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, planet);
            factory.ruinPool = new RuinData[8]; factory.ruinCursor = 2;
            factory.ruinPool[1] = new RuinData { id = 1, modelIndex = 406, lifeTime = -1, rot = Quaternion.identity };
            var game = Uninitialized<GameData>();
            game.galaxy = new GalaxyData { stars = new[] { new StarData { planets = new[] { planet } } } };
            GameMain.data = game;
            var adapter = new RuinSnapshotAdapter(new AuthorityEpoch(33, 44));
            var scope = new ScopeKey(PoolKind.Ruin, 101); var members = new List<ObjectKey>();
            TestAssert.IsTrue(adapter.TryReadMembers(scope, members)); var before = members.Single();
            TestAssert.IsTrue(adapter.TryReadState(before, out _));
            adapter.Retire(101, 1); // Native removal hook, before the next scan.
            members.Clear(); adapter.TryReadMembers(scope, members);
            TestAssert.AreEqual(before.Generation + 1, members.Single().Generation);
            TestAssert.IsFalse(adapter.TryReadState(before, out _));
            factory.ruinPool[1].id = 0; members.Clear(); adapter.TryReadMembers(scope, members);
            TestAssert.IsEmpty(members);
        }
        finally { GameMain.data = previous; }
    }
}
