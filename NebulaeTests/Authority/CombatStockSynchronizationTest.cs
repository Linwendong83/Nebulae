using HarmonyLib;
using global::NebulaeModel.Authority;
using global::NebulaeModel.DataStructures;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;
using NebulaePatcher.Patches.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using CollectionAssert = Microsoft.VisualStudio.TestTools.UnitTesting.CollectionAssert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class CombatStockSynchronizationTest
{
    private GameData previousGame = null!;
    private MultiplayerSession previousSession = null!;
    private bool wasLoaded;
    private int[] previousStackCounts = null!;
    private bool[] previousAmmoItems = null!;
    private bool[] previousBombItems = null!;
    private bool[] previousFighterItems = null!;
    private Harmony harmony = null!;
    private Player player = null!;
    private CombatAuthorityManager authority = null!;

    [TestInitialize]
    public void Initialize()
    {
        previousGame = GameMain.data;
        previousSession = Multiplayer.Session;
        wasLoaded = StorageComponent.staticLoaded;
        previousStackCounts = StorageComponent.itemStackCount;
        previousAmmoItems = StorageComponent.itemIsAmmo;
        previousBombItems = StorageComponent.itemIsBomb;
        previousFighterItems = StorageComponent.itemIsFighter;
        StorageComponent.staticLoaded = true;
        StorageComponent.itemStackCount = Enumerable.Repeat(100, 12000).ToArray();
        StorageComponent.itemIsAmmo = new bool[12000];
        StorageComponent.itemIsBomb = new bool[12000];
        StorageComponent.itemIsFighter = new bool[12000];
        StorageComponent.itemIsAmmo[1601] = true;
        StorageComponent.itemIsAmmo[1602] = true;
        StorageComponent.itemIsBomb[1128] = true;
        StorageComponent.itemIsBomb[1129] = true;
        StorageComponent.itemIsFighter[5101] = true;
        StorageComponent.itemIsFighter[5102] = true;
        harmony = new Harmony("nebulae.tests.combat-stock-synchronization");
        // Only fleet asset setup needs Unity; storage operations and combat facts use real game bodies.
        harmony.Patch(AccessTools.Method(typeof(CombatModuleComponent), "Setup", [typeof(int), typeof(GameData)]),
            prefix: new HarmonyMethod(typeof(CombatStockSynchronizationTest), nameof(SkipFleetAssets)));
        var game = Uninitialized<GameData>();
        player = Uninitialized<Player>();
        var mecha = Uninitialized<Mecha>();
        SetBackingField(player, "mecha", mecha);
        SetBackingField(game, "mainPlayer", player);
        player.isAlive = true;
        mecha.ammoStorage = new StorageComponent(3) { type = EStorageType.Ammo };
        mecha.bombStorage = new StorageComponent(1) { type = EStorageType.Bomb };
        mecha.fighterStorage = new StorageComponent(5) { type = EStorageType.Fighter };
        mecha.groundCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] };
        mecha.spaceCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] };
        GameMain.data = game;
        authority = new CombatAuthorityManager();
        var session = Uninitialized<MultiplayerSession>();
        session.World = new SimulatedWorld();
        session.LocalPlayer = new LocalPlayer { Data = new PlayerData(2, 103) };
        session.Authority = new AuthoritySessionState();
        session.Authority.BeginAuthorityWorld(new AuthorityEpoch(1, 2), isHost: false);
        session.AuthorityRuntime = new AuthoritySession(session.Authority);
        session.CombatAuthority = authority;
        session.Client = Uninitialized<global::NebulaeNetwork.Client>();
        Multiplayer.Session = session;
        harmony.Patch(AccessTools.Method(typeof(StorageComponent), nameof(StorageComponent.NotifyStorageChange)),
            postfix: new HarmonyMethod(typeof(PlayerCombatAuthority_Patch), nameof(PlayerCombatAuthority_Patch.PersonalStockChanged_Postfix)));
        harmony.Patch(AccessTools.Method(typeof(StorageComponent), nameof(StorageComponent.SetFilter)),
            postfix: new HarmonyMethod(typeof(PlayerCombatAuthority_Patch), nameof(PlayerCombatAuthority_Patch.PersonalStockFilterChanged_Postfix)));
    }

    [TestCleanup]
    public void Cleanup()
    {
        harmony.UnpatchSelf();
        StorageComponent.staticLoaded = wasLoaded;
        StorageComponent.itemStackCount = previousStackCounts;
        StorageComponent.itemIsAmmo = previousAmmoItems;
        StorageComponent.itemIsBomb = previousBombItems;
        StorageComponent.itemIsFighter = previousFighterItems;
        GameMain.data = previousGame;
        Multiplayer.Session = previousSession;
    }

    [TestMethod]
    public void ReconnectedActorCannotInheritOrReapplyThePreviousConnectionsDebitAcknowledgements()
    {
        AuthorityPlayerCombatStatePacket Packet(ulong connection, long revision, double debit, long command)
        {
            var writer = new NetDataWriter();
            new MechaFightData(player).Serialize(writer);
            return new AuthorityPlayerCombatStatePacket
            {
                EpochHigh = 1,
                EpochLow = 2,
                PlayerId = 2,
                ActorConnection = connection,
                CombatRevision = revision,
                CoreEnergyDebitTotal = debit,
                LastCommandSequence = command,
                IsAlive = true,
                CombatData = writer.CopyData()
            };
        }
        player.mecha.coreEnergy = 100;
        Multiplayer.Session.Authority.SetConnection(new ConnectionEpoch(1), 2);
        TestAssert.IsTrue(authority.ApplyState(Packet(1, 10, 10, 2121)));
        TestAssert.AreEqual(90d, player.mecha.coreEnergy);
        TestAssert.IsFalse(authority.ApplyState(Packet(1, 11, 3, 2121)));
        TestAssert.AreEqual(90d, player.mecha.coreEnergy);
        TestAssert.AreEqual(10d, authority.CoreDebitAcknowledged);
        Multiplayer.Session.Authority.SetConnection(new ConnectionEpoch(2), 2);
        TestAssert.IsTrue(authority.ApplyState(Packet(2, 1, 3, 4)));
        TestAssert.AreEqual(3d, authority.CoreDebitAcknowledged);
        TestAssert.AreEqual(4L, authority.LastAppliedCommand);
        TestAssert.AreEqual(87d, player.mecha.coreEnergy);
        TestAssert.IsTrue(authority.ApplyState(Packet(1, 99, 100, 9999)));
        TestAssert.AreEqual(87d, player.mecha.coreEnergy);
        TestAssert.AreEqual(4L, authority.LastAppliedCommand);
    }

    [TestMethod]
    [DataRow(EStorageType.Ammo, 1601)]
    [DataRow(EStorageType.Bomb, 1128)]
    [DataRow(EStorageType.Fighter, 5101)]
    public void CombatFactsKeepTheStorageBoundByAnOpenMechaWindow(EStorageType type, int itemId)
    {
        var bound = Stock(type);
        var grids = bound.grids;
        var changes = 0;
        bound.onStorageChange += () => changes++;
        bound.searchStart = 2;
        bound.lastFullItem = itemId;
        bound.lastEmptyItem = itemId;

        Apply(type, itemId, count: 20, revision: 1);

        TestAssert.AreSame(bound, Stock(type), "An open UIStorageGrid must keep writing to the live mecha storage.");
        TestAssert.AreSame(grids, bound.grids);
        TestAssert.AreEqual(20, bound.grids[0].count);
        TestAssert.AreEqual(itemId, bound.grids[0].filter);
        TestAssert.AreEqual(40, bound.grids[0].inc);
        TestAssert.IsTrue(bound.changed, "The existing grid must refresh after applying combat facts.");
        TestAssert.AreEqual(0, bound.searchStart);
        TestAssert.AreEqual(-1, bound.lastFullItem);
        TestAssert.AreEqual(-1, bound.lastEmptyItem);
        TestAssert.AreEqual(0, changes, "Host facts must not be mistaken for a personal stock transfer.");

        TestAssert.AreEqual(5, bound.AddItem(itemId, 5, 0, 1, 10, out _));
        TestAssert.AreEqual(25, Stock(type).grids[0].count);
        TestAssert.AreEqual(1, changes, "The original storage subscribers must still observe personal transfers.");
    }

    [TestMethod]
    [DataRow(EStorageType.Ammo, 1601)]
    [DataRow(EStorageType.Bomb, 1128)]
    [DataRow(EStorageType.Fighter, 5101)]
    public void DepositsThroughAnOpenWindowSurviveDelayedFactsAndCheckpointAcknowledgement(EStorageType type, int itemId)
    {
        var bound = Stock(type);
        Apply(type, itemId, count: 20, revision: 1);
        TestAssert.AreEqual(5, bound.AddItem(itemId, 5, 0, 1, 10, out _));
        authority.MarkPersonalStockChange(bound);

        Apply(type, itemId, count: 19, revision: 2);
        TestAssert.AreEqual(24, Stock(type).grids[0].count, "Keep the deposit and apply the host's one-item consumption.");
        TestAssert.AreSame(bound, Stock(type));
        authority.RememberPersonalCheckpoint(10);

        Apply(type, itemId, count: 18, revision: 3);
        TestAssert.AreEqual(23, bound.grids[0].count);
        Apply(type, itemId, count: 23, revision: 4, personalRevision: 10);
        TestAssert.AreEqual(23, bound.grids[0].count, "Acknowledging the deposit must neither lose nor duplicate it.");
        Apply(type, itemId, count: 22, revision: 5, personalRevision: 10);
        TestAssert.AreEqual(22, bound.grids[0].count, "A confirmed deposit must not refund later combat consumption.");
        Apply(type, itemId, count: 22, revision: 5, personalRevision: 10);
        TestAssert.AreEqual(22, bound.grids[0].count, "Duplicate facts must be harmless.");
        TestAssert.AreSame(bound, Stock(type));
    }

    [TestMethod]
    [DataRow(EStorageType.Ammo, 1601)]
    [DataRow(EStorageType.Bomb, 1128)]
    [DataRow(EStorageType.Fighter, 5101)]
    public void FilterOnlyEditsSurviveDelayedFactsAndAcknowledgement(EStorageType type, int itemId)
    {
        Apply(type, itemId, count: 0, revision: 1);
        var stock = Stock(type);
        var notifications = 0;
        stock.onStorageChange += () => notifications++;
        stock.SetFilter(0, 0);
        TestAssert.IsTrue(Read<bool>("personalStocksDirty"), "SetFilter has no native storage-change notification.");
        TestAssert.AreEqual(0, notifications);

        Apply(type, itemId, count: 0, revision: 2);
        TestAssert.AreEqual(0, stock.grids[0].filter);
        TestAssert.AreEqual(0, stock.grids[0].itemId);
        TestAssert.AreEqual(0, stock.grids[0].stackSize);
        authority.RememberPersonalCheckpoint(10);
        Apply(type, itemId, count: 0, revision: 3);
        TestAssert.AreEqual(0, stock.grids[0].filter);
        Apply(EmptyFight(), revision: 4, personalRevision: 10);
        TestAssert.AreEqual(0, stock.grids[0].filter);

        stock.SetFilter(0, itemId);
        Apply(EmptyFight(), revision: 5, personalRevision: 10);
        TestAssert.AreEqual(itemId, stock.grids[0].filter, "Setting an empty filter is also a personal edit.");
        TestAssert.AreEqual(100, stock.grids[0].stackSize);
        TestAssert.AreSame(stock, Stock(type));
    }

    [TestMethod]
    [DataRow(EStorageType.Ammo, 1601, 1602)]
    [DataRow(EStorageType.Bomb, 1128, 1129)]
    [DataRow(EStorageType.Fighter, 5101, 5102)]
    public void TakingTheLastItemAndReplacingItsFilterSurvivesHostFacts(EStorageType type, int oldItem, int newItem)
    {
        Apply(type, oldItem, count: 20, revision: 1);
        var stock = Stock(type);
        var item = 0; var count = 0;
        stock.TakeItemFromGrid(0, ref item, ref count, out var inc);
        TestAssert.AreEqual(20, count);
        TestAssert.AreEqual(40, inc);
        stock.SetFilter(0, 0); // UIStorageGrid.HandTake clears the filter after NotifyStorageChange.
        TestAssert.AreEqual(8, stock.AddItem(newItem, 8, 0, 1, 24, out _));
        stock.SetFilter(0, newItem);

        Apply(type, oldItem, count: 20, revision: 2);
        TestAssert.AreEqual(newItem, stock.grids[0].filter);
        TestAssert.AreEqual(newItem, stock.grids[0].itemId);
        TestAssert.AreEqual(8, stock.grids[0].count);
        TestAssert.AreEqual(24, stock.grids[0].inc);
        authority.RememberPersonalCheckpoint(10);
        Apply(type, newItem, count: 8, revision: 3, personalRevision: 10);
        Apply(type, newItem, count: 7, revision: 4, personalRevision: 10);
        TestAssert.AreEqual(7, stock.grids[0].count);
        TestAssert.AreSame(stock, Stock(type));
    }

    [TestMethod]
    public void SwappingFilteredSlotsKeepsHostConsumptionAndProliferatorPoints()
    {
        var baseline = new StorageComponent(2) { type = EStorageType.Ammo };
        baseline.grids[0] = Grid(1601, 20);
        baseline.grids[1] = Grid(1602, 30);
        var current = new StorageComponent(2) { type = EStorageType.Ammo };
        current.grids[0] = Grid(1601, 18);
        current.grids[1] = Grid(1602, 28);
        var incoming = new StorageComponent(2) { type = EStorageType.Ammo };
        incoming.grids[0] = baseline.grids[1]; incoming.grids[1] = baseline.grids[0];

        var merged = CombatAuthorityManager.MergeStock(current, baseline, incoming);

        TestAssert.AreEqual(Grid(1602, 28), merged.grids[0]);
        TestAssert.AreEqual(Grid(1601, 18), merged.grids[1]);
        TestAssert.AreEqual(Grid(1601, 18), current.grids[0], "Merging must leave its inputs untouched.");
    }

    [TestMethod]
    public void AReplacementFreesLaterOutgoingItemsBeforeAddingEarlierItems()
    {
        var baseline = new StorageComponent(2) { type = EStorageType.Ammo };
        baseline.grids[0] = Grid(1601, 100); baseline.grids[1] = Grid(1602, 100);
        var incoming = new StorageComponent(2) { type = EStorageType.Ammo };
        incoming.grids[0] = Grid(1601, 100); incoming.grids[1] = Grid(1601, 100);

        var merged = CombatAuthorityManager.MergeStock(baseline, baseline, incoming);

        CollectionAssert.AreEqual(incoming.grids, merged.grids);
        TestAssert.AreEqual(Grid(1602, 100), baseline.grids[1]);
    }

    [TestMethod]
    public void AnUneditedFilterKeepsTheCurrentHostSetting()
    {
        var baseline = new StorageComponent(1) { type = EStorageType.Bomb };
        baseline.SetFilter(0, 1128);
        var current = new StorageComponent(1) { type = EStorageType.Bomb };
        current.SetFilter(0, 1129);

        var merged = CombatAuthorityManager.MergeStock(current, baseline, baseline);

        TestAssert.AreEqual(Grid(1129, 0), merged.grids[0]);
    }

    [TestMethod]
    [DataRow(5, 20, 10)] // A withdrawal cannot refund ammunition the host already consumed.
    [DataRow(90, 20, 40)] // A deposit cannot exceed real remaining capacity.
    public void InvalidTransfersLeaveAllInputsUntouched(int currentCount, int baselineCount, int incomingCount)
    {
        var current = new StorageComponent(1) { type = EStorageType.Bomb };
        current.grids[0] = Grid(1128, currentCount);
        var baseline = new StorageComponent(1) { type = EStorageType.Bomb };
        baseline.grids[0] = Grid(1128, baselineCount);
        var incoming = new StorageComponent(1) { type = EStorageType.Bomb };
        incoming.grids[0] = Grid(1128, incomingCount);

        TestAssert.ThrowsExactly<InvalidOperationException>(() => CombatAuthorityManager.MergeStock(current, baseline, incoming));

        TestAssert.AreEqual(Grid(1128, currentCount), current.grids[0]);
        TestAssert.AreEqual(Grid(1128, baselineCount), baseline.grids[0]);
        TestAssert.AreEqual(Grid(1128, incomingCount), incoming.grids[0]);
    }

    [TestMethod]
    public void AFailedPendingCheckpointRebaseDoesNotPartiallyApplyAndAValidPacketCanFollow()
    {
        var initial = EmptyFight();
        initial.AmmoStorage.grids[0] = Grid(1601, 20);
        initial.BombStorage.grids[0] = Grid(1128, 20);
        initial.Hp = 100;
        Apply(initial, revision: 1);
        var ammo = Stock(EStorageType.Ammo); var bombs = Stock(EStorageType.Bomb);
        ammo.AddItemStacked(1601, 5, 10, out _);
        var item = 1128; var count = 10;
        bombs.TakeTailItems(ref item, ref count, out _);
        authority.RememberPersonalCheckpoint(10);
        bombs.AddItemStacked(1128, 10, 20, out _); // Undo that withdrawal in a later checkpoint.
        authority.RememberPersonalCheckpoint(11);
        var observed = Read<StorageComponent[]>("observedStocks");
        var history = Read<Dictionary<long, StorageComponent[]>>("publishedStockHistory");
        var pending = history[10]; var pendingAmmo = pending[0].grids.ToArray();
        var invalid = EmptyFight(); invalid.AmmoStorage.grids[0] = Grid(1601, 19);
        invalid.BombStorage.grids[0] = Grid(1128, 5); invalid.Hp = 40;

        TestAssert.ThrowsExactly<InvalidOperationException>(() => Apply(invalid, revision: 2));

        TestAssert.AreEqual(100, player.mecha.hp);
        TestAssert.AreEqual(25, ammo.grids[0].count);
        TestAssert.AreEqual(20, bombs.grids[0].count);
        TestAssert.AreSame(observed, Read<StorageComponent[]>("observedStocks"));
        TestAssert.AreSame(pending, history[10]);
        CollectionAssert.AreEqual(pendingAmmo, history[10][0].grids);
        TestAssert.AreEqual(1L, authority.LastAppliedRevision);

        var valid = EmptyFight(); valid.AmmoStorage.grids[0] = Grid(1601, 18);
        valid.BombStorage.grids[0] = Grid(1128, 19); valid.Hp = 90;
        Apply(valid, revision: 2); // The rejected revision must still be applicable.
        TestAssert.AreEqual(23, ammo.grids[0].count);
        TestAssert.AreEqual(19, bombs.grids[0].count);
        TestAssert.AreEqual(23, history[10][0].grids[0].count);
        TestAssert.AreEqual(9, history[10][1].grids[0].count);
        TestAssert.AreEqual(90, player.mecha.hp);
        valid.AmmoStorage.grids[0] = Grid(1601, 23);
        Apply(valid, revision: 3, personalRevision: 11);
        Apply(valid, revision: 3, personalRevision: 11);
        TestAssert.AreEqual(23, ammo.grids[0].count);
        TestAssert.AreEqual(19, bombs.grids[0].count);
        TestAssert.IsEmpty(history);
    }

    [TestMethod]
    public void ARejectedHostCheckpointKeepsItsPackageEnergyAndAllStocksUntilAValidTransfer()
    {
        var baseline = EmptyFight(); baseline.AmmoStorage.grids[0] = Grid(1601, 20);
        baseline.BombStorage.grids[0] = Grid(1128, 20);
        player.mecha.ammoStorage.grids[0] = Grid(1601, 20);
        player.mecha.bombStorage.grids[0] = Grid(1128, 90);
        player.mecha.coreEnergy = 1234;
        var originalPackage = new StorageComponent(4);
        SetBackingField(player, "package", originalPackage);
        var data = new PlayerData(2, 103) { CombatAuthoritative = true };
        var actorType = AccessTools.Inner(typeof(CombatAuthorityManager), "Actor");
        var actor = Activator.CreateInstance(actorType, nonPublic: true)!;
        AccessTools.Field(actorType, "Player").SetValue(actor, player);
        AccessTools.Field(actorType, "Data").SetValue(actor, data);
        var baselines = (Dictionary<long, StorageComponent[]>)AccessTools.Field(actorType, "StockHistory").GetValue(actor);
        baselines[1] = [baseline.AmmoStorage, baseline.BombStorage, baseline.FighterStorage];
        var revisions = (Dictionary<long, long>)AccessTools.Field(actorType, "StockPersonalRevisions").GetValue(actor);
        revisions[1] = data.Life.Revision;
        Read<System.Collections.IDictionary>("actors").Add((ushort)2, actor);
        var personal = EmptyFight(); personal.AmmoStorage.grids[0] = Grid(1601, 25);
        personal.BombStorage.grids[0] = Grid(1128, 40);
        var package = new StorageComponent(4); package.AddItemStacked(1101, 17, 34, out _);
        var snapshot = new PlayerData(2, 103) { Mecha = new MechaData { Inventory = package, CoreEnergy = 5678 } };

        TestAssert.IsFalse(authority.ValidatePersonalStocks(2, 1, personal));
        TestAssert.ThrowsExactly<InvalidOperationException>(() => authority.AcceptCheckpoint(2, data, snapshot, 0, 0,
            [], [], false, personal, combatRevision: 1));
        TestAssert.AreSame(originalPackage, player.package);
        TestAssert.AreEqual(1234d, player.mecha.coreEnergy);
        TestAssert.AreEqual(Grid(1601, 20), player.mecha.ammoStorage.grids[0]);
        TestAssert.AreEqual(Grid(1128, 90), player.mecha.bombStorage.grids[0]);

        personal.BombStorage.grids[0] = Grid(1128, 20);
        TestAssert.IsTrue(authority.ValidatePersonalStocks(2, 1, personal));
        authority.AcceptCheckpoint(2, data, snapshot, 0, 0, [], [], false, personal, combatRevision: 1);
        TestAssert.AreSame(package, player.package);
        TestAssert.AreEqual(17, player.package.GetItemCount(1101));
        TestAssert.AreEqual(5678d, player.mecha.coreEnergy);
        TestAssert.AreEqual(Grid(1601, 25), player.mecha.ammoStorage.grids[0]);
        TestAssert.AreEqual(Grid(1128, 90), player.mecha.bombStorage.grids[0]);
    }

    [TestMethod]
    public void AuthorityFilterApplicationAndUnrelatedStorageDoNotPublishPersonalTransfers()
    {
        var stock = player.mecha.bombStorage;
        Multiplayer.Session.AuthorityRuntime.ApplyContext.Run(new ApplyScope(new ScopeKey(PoolKind.PlayerCombat, 0)),
            () => stock.SetFilter(0, 1128));
        TestAssert.IsFalse(Read<bool>("personalStocksDirty"));
        new StorageComponent(1) { type = EStorageType.Bomb }.SetFilter(0, 1128);
        TestAssert.IsFalse(Read<bool>("personalStocksDirty"));
        stock.SetFilter(0, 0);
        TestAssert.IsTrue(Read<bool>("personalStocksDirty"));
    }

    private T Read<T>(string field) => (T)AccessTools.Field(typeof(CombatAuthorityManager), field).GetValue(authority);
    private static StorageComponent.GRID Grid(int item, int count) => new()
    { itemId = item, filter = item, count = count, inc = count * 2, stackSize = 100 };
    private MechaFightData EmptyFight() => new(player)
    {
        AmmoStorage = new StorageComponent(3) { type = EStorageType.Ammo },
        BombStorage = new StorageComponent(1) { type = EStorageType.Bomb },
        FighterStorage = new StorageComponent(5) { type = EStorageType.Fighter }
    };

    private StorageComponent Stock(EStorageType type) => type switch
    {
        EStorageType.Ammo => player.mecha.ammoStorage,
        EStorageType.Bomb => player.mecha.bombStorage,
        _ => player.mecha.fighterStorage
    };

    [TestMethod]
    public void ApplyingACachedFightCheckpointRebindsModulesToTheLoadedWorld()
    {
        var oldSector = Uninitialized<SpaceSector>();
        oldSector.enemyCursor = 1;
        var oldHistory = new GameHistoryData();
        GameMain.data.spaceSector = oldSector; GameMain.data.history = oldHistory;
        var writer = new NetDataWriter(); EmptyFight().Serialize(writer);
        var checkpoint = new MechaFightData(); checkpoint.Deserialize(new NetDataReader(writer.CopyData()));
        var loadedSector = Uninitialized<SpaceSector>();
        loadedSector.enemyCursor = 1; loadedSector.enemyPool = new EnemyData[1];
        var loadedHistory = new GameHistoryData();
        GameMain.data.spaceSector = loadedSector; GameMain.data.history = loadedHistory;
        checkpoint.UpdateMech(player);
        TestAssert.AreSame(loadedSector, player.mecha.spaceCombatModule.sector);
        TestAssert.AreSame(loadedSector, player.mecha.groundCombatModule.sector);
        TestAssert.AreSame(loadedHistory, player.mecha.spaceCombatModule.history);
        TestAssert.AreSame(loadedHistory, player.mecha.groundCombatModule.history);
        TestAssert.IsFalse(player.mecha.spaceCombatModule.DiscoverSpaceEnemy(default, true),
            "The native search must use the live pool instead of the released menu pool.");
    }

    [TestMethod]
    [DataRow(EStorageType.Ammo, 1601)]
    [DataRow(EStorageType.Bomb, 1128)]
    [DataRow(EStorageType.Fighter, 5101)]
    public void FirstCombatFactsPreserveDepositsMadeAfterRestoringThePersonalBaseline(EStorageType type, int item)
    {
        authority.InitializePersonalBaseline((PlayerData)Multiplayer.Session.LocalPlayer.Data);
        var stock = Stock(type);
        TestAssert.AreEqual(5, stock.AddItem(item, 5, 0, 1, 10, out _));
        authority.MarkPersonalStockChange(stock);
        Apply(type, item, 0, 1);
        TestAssert.AreEqual(5, stock.GetItemCount(item));
        Apply(type, item, 0, 1);
        TestAssert.AreEqual(5, stock.GetItemCount(item));
    }

    private void Apply(EStorageType type, int itemId, int count, long revision, long personalRevision = 0)
    {
        var fight = EmptyFight();
        var stock = type == EStorageType.Ammo ? fight.AmmoStorage : type == EStorageType.Bomb ? fight.BombStorage : fight.FighterStorage;
        stock.grids[0] = new StorageComponent.GRID { itemId = itemId, filter = itemId, count = count, stackSize = 100, inc = count * 2 };
        Apply(fight, revision, personalRevision);
    }

    private void Apply(MechaFightData fight, long revision, long personalRevision = 0)
    {
        var writer = new NetDataWriter();
        fight.Serialize(writer);
        TestAssert.IsTrue(authority.ApplyState(new AuthorityPlayerCombatStatePacket
        {
            EpochHigh = Multiplayer.Session.Authority.Epoch.High,
            EpochLow = Multiplayer.Session.Authority.Epoch.Low,
            PlayerId = 2,
            ActorConnection = 1,
            CombatRevision = revision,
            PersonalRevision = personalRevision,
            IsAlive = true,
            CombatData = writer.CopyData()
        }));
    }

    [TestMethod]
    public void RepeatedSerializationOfTheSameFightDataProducesIdenticalBytes()
    {
        // The host rebuilds a player's fight data every tick and re-serializes it every few ticks,
        // discarding most of the results, so the export buffer is reused rather than reallocated.
        // Reuse must not move a single byte: the packet is compared byte for byte against the last
        // published one, so a stray byte would either hide a real change or invent one.
        var wide = Fight(3);
        var narrow = Fight(1);
        var reference = new NetDataWriter(); wide.Serialize(reference);
        var expected = reference.CopyData();
        TestAssert.IsNotEmpty(expected);

        for (var i = 0; i < 3; i++)
        {
            var again = new NetDataWriter(); wide.Serialize(again);
            CollectionAssert.AreEqual(expected, again.CopyData(), "A reused buffer must reproduce the same bytes.");

            // A shorter record written into the same buffer is the case that would expose a missing
            // reset: without one, the longer record's tail survives and is shipped as if it were data.
            var shorter = new NetDataWriter(); narrow.Serialize(shorter);
            TestAssert.IsLessThan(expected.Length, shorter.CopyData().Length, "The two records must differ in length.");

            var after = new NetDataWriter(); wide.Serialize(after);
            CollectionAssert.AreEqual(expected, after.CopyData(), "A shorter record must not leave its tail behind.");
        }

        // The bytes are not merely stable, they are still a faithful record: round-tripping the
        // reused serialization must recover the same state.
        var round = new NetDataWriter(); wide.Serialize(round);
        var restored = new MechaFightData();
        restored.Deserialize(new NetDataReader(round.CopyData()));
        TestAssert.AreEqual(wide.Hp, restored.Hp);
        TestAssert.AreEqual(wide.AmmoBulletCount, restored.AmmoBulletCount);
        CollectionAssert.AreEqual(wide.AmmoStorage.grids, restored.AmmoStorage.grids,
            "The round trip must recover the storage the reused buffer encoded.");
    }

    [TestMethod]
    public void AHostOnlyCounterDoesNotMakeAnIdlePlayerLookChanged()
    {
        // tickSinceLastEnemy is advanced by the host's own CombatModuleComponent.GameTick every tick
        // and is never read on the receiving side, so shipping the live value would make every packet
        // differ every six ticks and defeat the change gate for a player merely standing by.
        var fight = Fight(3);
        fight.GroundCombatModule.tickSinceLastEnemy = 7;
        fight.SpaceCombatModule.tickSinceLastEnemy = 900;
        var standing = CombatAuthorityManager.SerializeReplicableFightData(fight);
        fight.GroundCombatModule.tickSinceLastEnemy = 8;
        fight.SpaceCombatModule.tickSinceLastEnemy = 901;
        var waiting = CombatAuthorityManager.SerializeReplicableFightData(fight);
        TestAssert.AreSame(standing, CombatAuthorityManager.SerializeReplicableFightData(fight, standing),
            "An unchanged export must reuse the immutable published payload.");
        var changedFight = Fight(1);
        var changedPayload = CombatAuthorityManager.SerializeReplicableFightData(changedFight, standing);
        TestAssert.AreNotSame(standing, changedPayload);
        CollectionAssert.AreEqual(standing, CombatAuthorityManager.SerializeReplicableFightData(fight),
            "Writing a shorter record must not modify an earlier published array or leave a stale tail.");

        CollectionAssert.AreEqual(standing, waiting,
            "A counter the receiver never reads must not change what goes on the wire.");
        // The host's own fleet AI still needs the real counter, and the record aliases the live
        // modules, so the normalization must be undone rather than left in place.
        TestAssert.AreEqual(8, fight.GroundCombatModule.tickSinceLastEnemy);
        TestAssert.AreEqual(901, fight.SpaceCombatModule.tickSinceLastEnemy);

        // Sanity: without the normalization the counter alone really does split the payload, so the
        // assertion above is not passing by accident.
        var before = new NetDataWriter(); var one = Fight(3); one.GroundCombatModule.tickSinceLastEnemy = 7; one.Serialize(before);
        var after = new NetDataWriter(); var two = Fight(3); two.GroundCombatModule.tickSinceLastEnemy = 8; two.Serialize(after);
        CollectionAssert.AreNotEqual(before.CopyData(), after.CopyData(),
            "The counter alone must be enough to make an unnormalized payload differ.");
    }

    private static MechaFightData Fight(int ammoGrids)
    {
        var fight = new MechaFightData
        {
            Hp = 302400,
            AmmoItemId = 1601,
            AmmoInc = 40,
            AmmoBulletCount = 40,
            AmmoSelectSlot = 1,
            LaserEnergy = 600,
            AmmoStorage = new StorageComponent(3) { type = EStorageType.Ammo, size = ammoGrids },
            BombStorage = new StorageComponent(1) { type = EStorageType.Bomb },
            FighterStorage = new StorageComponent(5) { type = EStorageType.Fighter },
            GroundCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] },
            SpaceCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] }
        };
        for (var i = 0; i < ammoGrids; i++)
            fight.AmmoStorage.grids[i] = new StorageComponent.GRID { itemId = 1601, filter = 1601, count = 10 + i, stackSize = 100, inc = 20 + i };
        return fight;
    }

    [TestMethod]
    public void DelayedCombatFactsCannotUndoTheLocalWeaponModes()
    {
        player.mecha.ammoSelectSlot = 2;
        player.mecha.ammoSelectSlotState = 2;
        player.mecha.laserActiveState = 1;
        player.mecha.bombActive = true;
        var oldFight = EmptyFight();
        oldFight.AmmoSelectSlot = 1;
        oldFight.AmmoSelectSlotState = 1;
        oldFight.LaserActiveState = 2;
        oldFight.BombActive = false;
        Apply(oldFight, revision: 1);
        TestAssert.AreEqual(2, player.mecha.ammoSelectSlot);
        TestAssert.AreEqual(2, player.mecha.ammoSelectSlotState);
        TestAssert.AreEqual(1, player.mecha.laserActiveState);
        TestAssert.IsTrue(player.mecha.bombActive);
    }

    private static bool SkipFleetAssets() => false;
}
