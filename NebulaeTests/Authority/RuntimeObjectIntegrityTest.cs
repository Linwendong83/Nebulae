using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class RuntimeObjectIntegrityTest
{
    [TestMethod]
    public void ActualPoolCorruptionIsDetectedWhilePredictionPoseIsAllowed()
    {
        var previous = GameMain.data;
        try
        {
            var factory = Uninitialized<PlanetFactory>();
            var planet = new PlanetData { id = 101, factory = factory, factoryLoaded = true };
            AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, planet);
            factory.entityCursor = 2;
            factory.entityPool = new EntityData[2];
            factory.entityPool[1] = new EntityData { id = 1, combatStatId = 1 };
            factory.skillSystem = Uninitialized<SkillSystem>();
            var stats = factory.skillSystem.combatStats = new DataPool<CombatStat>(); stats.Reset();
            ref var stat = ref stats.Add();
            stat.objectType = (int)EObjectType.Entity; stat.objectId = 1; stat.astroId = stat.originAstroId = 101;
            stat.hp = 75; stat.hpMax = 100; stat.hpRecover = 1;
            var data = Uninitialized<GameData>();
            data.galaxy = new GalaxyData { stars = new[] { new StarData { planets = new[] { planet } } } };
            GameMain.data = data;
            var key = ObjectKey.Create(new AuthorityEpoch(7, 11), PoolKind.Entity, 101, 1, 1);
            var verifier = new RuntimeObjectVerifier();
            TestAssert.IsTrue(FactoryCombatStateCodec.TryEncode(new FactoryCombatState(true, 75, 100, 1, 0, false, 0, 0, 0, 0), out var confirmed));
            TestAssert.AreEqual(RuntimeObjectIntegrity.Matches, verifier.VerifyObject(key, confirmed));
            stat.localPos = new VectorLF3(1, 2, 3);
            TestAssert.AreEqual(RuntimeObjectIntegrity.Matches, verifier.VerifyObject(key, confirmed));
            stat.hp = 999;
            TestAssert.AreEqual(RuntimeObjectIntegrity.Drift, verifier.VerifyObject(key, confirmed));
            stat.hp = 75; stat.objectId = 2;
            TestAssert.AreEqual(RuntimeObjectIntegrity.Drift, verifier.VerifyObject(key, confirmed));
            stat.objectId = 1; factory.entityPool[1].id = 0;
            TestAssert.AreEqual(RuntimeObjectIntegrity.Drift, verifier.VerifyObject(key, confirmed));
            planet.factoryLoaded = false;
            TestAssert.AreEqual(RuntimeObjectIntegrity.Unavailable, verifier.VerifyObject(key, confirmed));
        }
        finally { GameMain.data = previous; }
    }
}
