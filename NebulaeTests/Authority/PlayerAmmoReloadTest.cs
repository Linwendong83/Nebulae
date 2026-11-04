using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Packets.Combat;
using NebulaeModel.Utils;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class PlayerAmmoReloadTest
{
    private static ItemProtoSet fixtureItems = null!;
    private bool previousLoaded;
    private int[] previousStacks = null!;
    private Harmony harmony = null!;

    [TestInitialize]
    public void Setup()
    {
        var items = Uninitialized<ItemProtoSet>();
        items.dataArray = [new ItemProto { ID = 1601, HpMax = 30, AmmoType = EAmmoType.Bullet },
            new ItemProto { ID = 1602, HpMax = 12, AmmoType = EAmmoType.Bullet }];
        AccessTools.Field(typeof(ProtoSet<ItemProto>), "dataIndices").SetValue(items, new Dictionary<int, int> { [1601] = 0, [1602] = 1 });
        fixtureItems = items;
        previousLoaded = StorageComponent.staticLoaded; previousStacks = StorageComponent.itemStackCount;
        StorageComponent.staticLoaded = true; StorageComponent.itemStackCount = Enumerable.Repeat(100, 12000).ToArray();
        harmony = new Harmony("nebulae.tests.native-ammo-reload");
        // Other tests install native method detours before this fixture. Bind the external
        // database at the LoadAmmo call sites, so a previously JIT-inlined getter cannot
        // attempt to load Unity assets in the standalone test runner.
        harmony.Patch(AccessTools.Method(typeof(Mecha), nameof(Mecha.LoadAmmo)),
            transpiler: new HarmonyMethod(typeof(PlayerAmmoReloadTest), nameof(UseTestItemTable)));
        harmony.Patch(AccessTools.Method(typeof(Mecha), nameof(Mecha.AddConsumptionStat)),
            prefix: new HarmonyMethod(typeof(PlayerAmmoReloadTest), nameof(SkipStatistics)));
    }

    private static bool SkipStatistics() => false;
    private static ItemProtoSet ItemTable() => fixtureItems;
    private static IEnumerable<CodeInstruction> UseTestItemTable(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var code in instructions)
        {
            if (code.Calls(AccessTools.PropertyGetter(typeof(LDB), nameof(LDB.items))))
            { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(PlayerAmmoReloadTest), nameof(ItemTable)); }
            yield return code;
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        harmony.UnpatchSelf(); fixtureItems = null!;
        StorageComponent.staticLoaded = previousLoaded; StorageComponent.itemStackCount = previousStacks;
        PlayerWeaponResources.Active = false;
    }

    private static Mecha Create(int bullets, int magazines)
    {
        var player = Uninitialized<Player>();
        player.SetHiddenProperty(nameof(Player.planetData), new PlanetData { factory = Uninitialized<PlanetFactory>(), factoryLoaded = true });
        player.SetHiddenProperty(nameof(Player.package), new StorageComponent(2));
        var mecha = new Mecha
        {
            player = player,
            ammoStorage = new StorageComponent(3),
            bombStorage = new StorageComponent(1),
            ammoItemId = 1601,
            ammoSelectSlot = 1,
            ammoBulletCount = bullets
        };
        mecha.ammoStorage.grids[0] = new StorageComponent.GRID { itemId = 1601, count = magazines, stackSize = 100 };
        return mecha;
    }

    [TestMethod]
    [DataRow(2, 2, 1, 2)]
    [DataRow(1, 2, 30, 1)]
    [DataRow(1, 0, 0, 0)]
    public void AShotPublishesTheRefilledMagazineOrARealEmptyState(int bullets, int magazines, int expectedBullets, int expectedStock)
    {
        var mecha = Create(bullets, magazines);
        PlayerWeaponResources.ConsumeRound(mecha);
        TestAssert.AreEqual(expectedBullets, mecha.ammoBulletCount);
        TestAssert.AreEqual(expectedStock, mecha.ammoStorage.grids[0].count);
    }

    [TestMethod]
    public void NativeRefillCanSelectAnotherAmmunitionSlot()
    {
        var mecha = Create(1, 0);
        mecha.ammoStorage.grids[1] = new StorageComponent.GRID { itemId = 1602, count = 1, stackSize = 100 };
        PlayerWeaponResources.ConsumeRound(mecha);
        TestAssert.AreEqual(1602, mecha.ammoItemId); TestAssert.AreEqual(2, mecha.ammoSelectSlot);
        TestAssert.AreEqual(12, mecha.ammoBulletCount); TestAssert.AreEqual(0, mecha.ammoStorage.grids[1].count);
    }

    [TestMethod]
    public void PredictionRefillsOnlyItsCopyAndSettlementDoesNotConsumeAnotherMagazine()
    {
        var mecha = Create(1, 2); var resources = new PlayerWeaponResources(); var shot = new PlayerShotId(2, 1, 1);
        using (resources.Enter(mecha))
        {
            resources.Reserve(shot, EAmmoType.Bullet, false, 1601, 1);
            PlayerWeaponResources.ConsumeRound(mecha);
            TestAssert.AreEqual(30, mecha.ammoBulletCount);
        }
        TestAssert.AreEqual(1, mecha.ammoBulletCount); TestAssert.AreEqual(2, mecha.ammoStorage.grids[0].count);
        using (resources.Enter(mecha))
        { TestAssert.AreEqual(30, mecha.ammoBulletCount); TestAssert.AreEqual(1, mecha.ammoStorage.grids[0].count); }
        resources.Reject(shot);
        using (resources.Enter(mecha))
        { TestAssert.AreEqual(1, mecha.ammoBulletCount); TestAssert.AreEqual(2, mecha.ammoStorage.grids[0].count); }
        resources.Reserve(shot, EAmmoType.Bullet, false, 1601, 1);
        resources.Settle(shot, 7);
        mecha.ammoBulletCount = 30; mecha.ammoStorage.grids[0].count = 1;
        resources.Applied(7);
        using (resources.Enter(mecha))
        { TestAssert.AreEqual(30, mecha.ammoBulletCount); TestAssert.AreEqual(1, mecha.ammoStorage.grids[0].count); }
    }
}
