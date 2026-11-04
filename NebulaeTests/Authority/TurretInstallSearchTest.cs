using HarmonyLib;
using global::NebulaeModel.Authority;
using global::NebulaeModel.Packets.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class TurretInstallSearchTest
{
    private static int refreshes;
    private static bool CountRefresh() { refreshes++; return false; }

    [TestMethod]
    public void FirstTurretInstallationAndPoolReplacementRebuildNativeSearchPairs()
    {
        var previousGame = GameMain.data; var previousSession = Multiplayer.Session;
        var harmony = new Harmony("nebulae.tests.turret-install-search");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(DefenseSystem), nameof(DefenseSystem.RefreshTurretSearchPair)),
                prefix: new HarmonyMethod(typeof(TurretInstallSearchTest), nameof(CountRefresh)));
            var factory = Uninitialized<PlanetFactory>();
            var planet = new PlanetData { id = 101, factory = factory, factoryLoaded = true };
            AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, planet);
            factory.entityPool = new EntityData[8]; factory.entityCursor = 2;
            factory.entityPool[1] = new EntityData { id = 1, modelIndex = 400, turretId = 1 };
            factory.enemyPool = new EnemyData[8]; factory.enemyCursor = 1;
            factory.defenseSystem = Uninitialized<DefenseSystem>(); factory.defenseSystem.factory = factory;
            factory.defenseSystem.turrets = new DataPool<TurretComponent>(); factory.defenseSystem.turrets.Reset();
            ref var turret = ref factory.defenseSystem.turrets.Add();
            turret.entityId = 1; turret.type = ETurretType.Missile; turret.searchPairCount = 9;
            factory.enemySystem = Uninitialized<EnemyDFGroundSystem>();
            factory.enemySystem.bases = new ObjectPool<DFGBaseComponent>(); factory.enemySystem.bases.Reset();
            var game = Uninitialized<GameData>();
            game.galaxy = new GalaxyData { stars = new[] { new StarData { planets = new[] { planet } } } };
            GameMain.data = game;
            var session = Uninitialized<MultiplayerSession>(); session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            var runtime = new AuthoritySession(new AuthoritySessionState()); session.AuthorityRuntime = runtime;
            Multiplayer.Session = session;
            var epoch = new AuthorityEpoch(91, 92); runtime.BeginAuthorityWorld(epoch, false);
            var scope = new ScopeKey(PoolKind.Turret, 101);
            var key = ObjectKey.Create(epoch, PoolKind.Turret, 101, 1, 1);
            TestAssert.IsTrue(TurretStateCodec.TryEncode(new TurretState { EntityId = 1, ModelIndex = 400, Type = (byte)ETurretType.Missile }, out var bytes));
            refreshes = 0;
            runtime.ApplyContext.Run(new ApplyScope(scope), () => runtime.TurretReplica.OnStateApplied(scope, key, 1, bytes));
            runtime.DarkFogReplica.FlushSearchReferences();
            TestAssert.AreEqual(ETargetType.Enemy, factory.defenseSystem.turrets.buffer[1].target.type, "An empty checkpoint retains the native turret target-kind invariant.");
            TestAssert.AreEqual(1, refreshes, "Installing a checkpoint must not leave the new turret with zero native search pairs.");
            runtime.ApplyContext.Run(new ApplyScope(scope), () => runtime.TurretReplica.OnStateApplied(scope, key, 2, bytes));
            runtime.DarkFogReplica.FlushSearchReferences();
            TestAssert.AreEqual(1, refreshes, "Ordinary checkpoints retain the existing search index.");
            factory.defenseSystem.turrets.SetCapacity(16);
            runtime.ApplyContext.Run(new ApplyScope(scope), () => runtime.TurretReplica.OnStateApplied(scope, key, 3, bytes));
            runtime.DarkFogReplica.FlushSearchReferences();
            TestAssert.AreEqual(2, refreshes);
        }
        finally { harmony.UnpatchSelf(); GameMain.data = previousGame; Multiplayer.Session = previousSession; }
    }
}
