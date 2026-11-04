using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Factory.Assembler;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;
using NebulaeWorld.GameStates;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class BattleSyncFixesTest
{
    private static FleetProtoSet fixtureFleets = null!;
    private static FleetProtoSet Fleets() => fixtureFleets;
    private static IEnumerable<CodeInstruction> UseFixtureFleets(IEnumerable<CodeInstruction> codes)
    {
        foreach (var code in codes)
        {
            if (code.Calls(AccessTools.PropertyGetter(typeof(LDB), nameof(LDB.fleets))))
            { code.opcode = System.Reflection.Emit.OpCodes.Call; code.operand = AccessTools.Method(typeof(BattleSyncFixesTest), nameof(Fleets)); }
            yield return code;
        }
    }

    [TestMethod]
    public void NativeSpaceConfigurationReplacementKeepsADisabledFleetDisabled()
    {
        var previousSession = Multiplayer.Session;
        var previousSmall = ItemProto.kFighterSpaceSmallIds; var previousLarge = ItemProto.kFighterSpaceLargeIds;
        using var runtime = new AuthoritySession(new AuthoritySessionState()); runtime.BeginAuthorityWorld(new AuthorityEpoch(7, 11), true);
        var harmony = new Harmony("nebulae.tests.disabled-space-configuration");
        try
        {
            ItemProto.kFighterSpaceSmallIds = [5111]; ItemProto.kFighterSpaceLargeIds = [5112];
            fixtureFleets = Uninitialized<FleetProtoSet>();
            fixtureFleets.dataArray = [new FleetProto { ID = 3,
                prefabDesc = new PrefabDesc { fleetPorts = [new FleetPortDesc { size = ECraftSize.Large }] } }];
            AccessTools.Field(typeof(ProtoSet<FleetProto>), "dataIndices").SetValue(fixtureFleets, new Dictionary<int, int> { [3] = 0 });
            harmony.Patch(AccessTools.Method(typeof(ModuleFleet), nameof(ModuleFleet.Create), [typeof(int)]),
                transpiler: new HarmonyMethod(typeof(BattleSyncFixesTest), nameof(UseFixtureFleets)));
            harmony.CreateClassProcessor(typeof(FleetConfiguration_Patch)).Patch();
            var session = Uninitialized<MultiplayerSession>(); session.AuthorityRuntime = runtime; session.Authority = runtime.Identity;
            session.Server = Uninitialized<global::NebulaeNetwork.Server>(); Multiplayer.Session = session;
            var module = new CombatModuleComponent
            {
                fleetCount = 1,
                isSpace = true,
                autoReplenishFleet = true,
                moduleFleets = [new ModuleFleet { protoId = 2, fleetEnabled = false, fighters = new ModuleFighter[4] }]
            };
            module.ChangeFleetConfig(0, 3, Uninitialized<StorageComponent>(), null!);
            TestAssert.IsFalse(module.moduleFleets[0].fleetEnabled);
            TestAssert.HasCount(1, module.moduleFleets[0].fighters);
            TestAssert.AreEqual(ECraftSize.Large, module.moduleFleets[0].fighters[0].size);
            TestAssert.AreEqual(0, module.moduleFleets[0].fighters[0].craftId);
        }
        finally
        {
            harmony.UnpatchSelf(); Multiplayer.Session = previousSession;
            ItemProto.kFighterSpaceSmallIds = previousSmall; ItemProto.kFighterSpaceLargeIds = previousLarge;
        }
    }

    [TestMethod]
    public void PlanetSpacePlanetRestoresTheRuinSubscription()
    {
        using var runtime = new AuthoritySession(new AuthoritySessionState());
        runtime.BeginAuthorityWorld(new AuthorityEpoch(7, 11), false);
        var ruin = new ScopeKey(PoolKind.Ruin, 101);
        runtime.UpdateStandingSubscriptions(101, false, JoinSyncManager.HasReplicaAdapter, 1);
        TestAssert.Contains(ruin, runtime.StandingScopes);
        runtime.UpdateStandingSubscriptions(0, true, JoinSyncManager.HasReplicaAdapter, 1);
        TestAssert.DoesNotContain(ruin, runtime.StandingScopes);
        runtime.UpdateStandingSubscriptions(101, false, JoinSyncManager.HasReplicaAdapter, 1);
        TestAssert.Contains(ruin, runtime.StandingScopes);
    }

    [TestMethod]
    public void OrphanedCraftReferencesCannotBeErasedByConfiguration()
    {
        var fighters = new[] { new ModuleFighter { itemId = 1001, count = 1 }, new ModuleFighter { craftId = 9 } };
        var module = new CombatModuleComponent
        {
            fleetCount = 1,
            autoReplenishFleet = true,
            moduleFleets = [new ModuleFleet { fighters = fighters, fleetEnabled = false }]
        };
        TestAssert.IsFalse(FleetConfiguration.TryPrepare(module, 0));
        TestAssert.AreSame(fighters, module.moduleFleets[0].fighters);
        TestAssert.AreEqual(1, fighters[0].count); TestAssert.AreEqual(9, fighters[1].craftId);
        TestAssert.IsFalse(module.moduleFleets[0].fleetEnabled);
        fighters[1].craftId = 0;
        TestAssert.IsTrue(FleetConfiguration.TryPrepare(module, 0));
        TestAssert.IsFalse(module.moduleFleets[0].fleetEnabled);
    }

    [TestMethod]
    public void DeployedFleetIsOnlyAskedToReturnAndKeepsItsLayout()
    {
        var oldData = GameMain.data;
        try
        {
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var sector = Uninitialized<SpaceSector>(); game.spaceSector = sector;
            sector.combatSpaceSystem = Uninitialized<CombatSpaceSystem>();
            var pool = sector.combatSpaceSystem.fleets = new DataPool<FleetComponent>(); pool.Reset();
            var id = pool.Add().id; pool.buffer[id] = new FleetComponent { id = id, craftId = 8, dispatch = true };
            var fighters = new[] { new ModuleFighter { itemId = 1001, count = 1, craftId = 9 } };
            var module = new CombatModuleComponent
            {
                sector = sector,
                isSpace = true,
                fleetCount = 1,
                moduleFleets = [new ModuleFleet { fleetId = id, fighters = fighters, fleetEnabled = false }]
            };
            TestAssert.IsFalse(FleetConfiguration.TryPrepare(module, 0));
            TestAssert.IsFalse(pool.buffer[id].dispatch);
            TestAssert.AreEqual(id, module.moduleFleets[0].fleetId);
            TestAssert.AreEqual(9, fighters[0].craftId); TestAssert.AreSame(fighters, module.moduleFleets[0].fighters);
        }
        finally { GameMain.data = oldData; }
    }

    [TestMethod]
    public void WholeBaseConfigurationCommitsInventoryAtomicallyAndPreservesDisabledState()
    {
        var oldLoaded = StorageComponent.staticLoaded; var oldStacks = StorageComponent.itemStackCount;
        try
        {
            StorageComponent.staticLoaded = true; StorageComponent.itemStackCount = Enumerable.Repeat(100, 12000).ToArray();
            var module = new CombatModuleComponent
            {
                fleetCount = 1,
                autoReplenishFleet = true,
                moduleFleets = [new ModuleFleet { fleetEnabled = false, fighters = [
                    new ModuleFighter { itemId = 1001, count = 1 }, new ModuleFighter { itemId = 1001, count = 1 }] }]
            };
            var stock = new StorageComponent(1); var bag = new StorageComponent(0);
            stock.grids[0] = new StorageComponent.GRID { itemId = 1001, count = 99, stackSize = 100 };
            TestAssert.IsFalse(FleetConfiguration.TryConfigureGround(module, 0, 1002, stock, bag, out _, i => i == 1002));
            TestAssert.AreEqual(99, stock.GetItemCount(1001));
            TestAssert.AreEqual(2, module.moduleFleets[0].fighters.Sum(f => f.count));
            stock.grids[0].count = 98;
            TestAssert.IsTrue(FleetConfiguration.TryConfigureGround(module, 0, 1002, stock, bag, out var credits, i => i == 1002));
            TestAssert.AreEqual(100, stock.GetItemCount(1001)); TestAssert.IsEmpty(credits);
            TestAssert.IsTrue(module.moduleFleets[0].fighters.All(f => f.count == 0 && f.itemId == 1002 && f.craftId == 0));
            TestAssert.IsFalse(module.moduleFleets[0].fleetEnabled);
        }
        finally { StorageComponent.staticLoaded = oldLoaded; StorageComponent.itemStackCount = oldStacks; }
    }

    [TestMethod]
    public void WholeFleetConfigurationHasItsOwnValidatedTransaction()
    {
        var intent = new FactoryProductIntent(FactoryInteractionKind.FleetConfigure, -5, item: 5101);
        TestAssert.IsTrue(FactoryProductIntent.TryDecode(intent.Encode(), out var decoded));
        TestAssert.AreEqual(intent.Kind, decoded.Kind); TestAssert.AreEqual(5101, decoded.Item);
        TestAssert.IsFalse(FactoryProductIntent.TryDecode(new FactoryProductIntent(intent.Kind, -6, item: 5101).Encode(), out _));
        TestAssert.IsFalse(FactoryProductIntent.TryDecode(new FactoryProductIntent(intent.Kind, -5, item: 5101, count: 1).Encode(), out _));
    }

    [TestMethod]
    public void SelectingTheCurrentGroundTypeDoesNotRecallADeployedFleet()
    {
        var module = new CombatModuleComponent
        {
            fleetCount = 1,
            moduleFleets = [new ModuleFleet { fleetId = 9, fighters = [new ModuleFighter { itemId = 1001, count = 1, craftId = 7 }] }]
        };
        TestAssert.IsTrue(FleetConfiguration.TryConfigureGround(module, 0, 1001, Uninitialized<StorageComponent>(),
            Uninitialized<StorageComponent>(), out var credits, i => i == 1001));
        TestAssert.IsEmpty(credits); TestAssert.AreEqual(9, module.moduleFleets[0].fleetId);
        TestAssert.AreEqual(7, module.moduleFleets[0].fighters[0].craftId);
    }

    private static CraftState State(long tick, ushort owner) => new(false, true, true, 5111, 60, 0, (byte)ECraftProto.Item, 0,
        100, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, tick, owner);

    [TestMethod]
    public void PlayerCheckpointsUseTwelveTicksAndBasesKeepTheirExistingCadence()
    {
        TestAssert.IsFalse(CraftStatePolicy.ShouldPublish(State(0, 7), State(11, 7)));
        TestAssert.IsTrue(CraftStatePolicy.ShouldPublish(State(0, 7), State(12, 7)));
        TestAssert.IsFalse(CraftStatePolicy.ShouldPublish(State(0, 0), State(119, 0)));
        TestAssert.IsTrue(CraftStatePolicy.ShouldPublish(State(0, 0), State(120, 0)));
        TestAssert.IsTrue(CraftStatePolicy.ShouldPublish(State(0, 7), State(1, 8)));
    }

    [TestMethod]
    public void ConfigurationAndWeaponGuardsInstallOnTheActualGameMethods()
    {
        var harmony = new Harmony("nebulae.tests.battle-sync-fixes");
        try
        {
            harmony.CreateClassProcessor(typeof(FleetConfiguration_Patch)).Patch();
            harmony.CreateClassProcessor(typeof(CraftWeaponPrediction_Patch)).Patch();
            foreach (var method in CraftWeaponPrediction_Patch.TargetMethods())
                TestAssert.Contains(harmony.Id, Harmony.GetPatchInfo(method).Owners);
        }
        finally { harmony.UnpatchSelf(); }
    }
}
