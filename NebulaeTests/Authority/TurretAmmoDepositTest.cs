using HarmonyLib;
using NebulaeModel.Authority;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class TurretAmmoDepositTest
{
    private static int requestedCount;

    private static bool CaptureDeposit(int wanted, ref bool __result)
    {
        requestedCount = wanted;
        __result = true;
        return false;
    }

    [TestMethod]
    [DataRow(10, 20, 47, 0, 0, 20)]
    [DataRow(90, 20, 47, 10, 27, 10)]
    [DataRow(100, 20, 47, 20, 47, 0)]
    public void HandDepositConservesAmmoAndIncBeforeTheCheckpoint(
        int packageCount, int handCount, int handInc, int remainingCount, int remainingInc, int depositCount)
    {
        var previousSession = Multiplayer.Session;
        var previousLoaded = StorageComponent.staticLoaded;
        var previousNeeds = ItemProto.turretNeeds;
        var harmony = new Harmony("nebulae.tests.turret-ammo-deposit");
        try
        {
            // Run the real UI prefix and native storage methods; only stop at the command boundary.
            harmony.Patch(AccessTools.Method(typeof(TurretAuthorityManager), nameof(TurretAuthorityManager.Deposit)),
                prefix: new HarmonyMethod(typeof(TurretAmmoDepositTest), nameof(CaptureDeposit)));
            StorageComponent.staticLoaded = true;
            ItemProto.turretNeeds = new int[16][];
            ItemProto.turretNeeds[(int)EAmmoType.Bullet] = [1601];
            var player = Uninitialized<Player>();
            var package = new StorageComponent(1);
            package.grids[0] = new StorageComponent.GRID { itemId = 1601, count = packageCount, inc = packageCount * 2, stackSize = 100 };
            SetBackingField(player, nameof(Player.package), package);
            player.SetHandItemId_Unsafe(1601);
            player.SetHandItemCount_Unsafe(handCount);
            player.SetHandItemInc_Unsafe(handInc);
            var ui = Uninitialized<UITurretWindow>();
            ui.player = player;
            ui.factory = Uninitialized<PlanetFactory>();
            SetBackingField(ui.factory, nameof(PlanetFactory.planet), new PlanetData { id = 101 });
            ui.defenseSystem = Uninitialized<DefenseSystem>();
            ui.defenseSystem.turrets = new DataPool<TurretComponent>();
            ui.defenseSystem.turrets.Reset();
            ref var turret = ref ui.defenseSystem.turrets.Add();
            turret.ammoType = EAmmoType.Bullet;
            SetField(ui, "_turretId", turret.id);
            var session = Uninitialized<MultiplayerSession>();
            session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            session.Authority = new AuthoritySessionState();
            session.Authority.BeginAuthorityWorld(new AuthorityEpoch(7, 11), false);
            session.Turrets = new TurretAuthorityManager();
            Multiplayer.Session = session;
            requestedCount = -1;

            TestAssert.IsFalse(TurretAuthorityUI_Patch.Ammo_Prefix(ui));

            TestAssert.AreEqual(packageCount + handCount, package.GetItemCount(1601) + player.inhandItemCount,
                "Moving hand ammo into the checkpointed package must credit it exactly once.");
            TestAssert.AreEqual(packageCount * 2 + handInc, package.grids.Sum(grid => grid.inc) + player.inhandItemInc,
                "The untransferred hand stack must keep its native increment split.");
            TestAssert.AreEqual(remainingCount, player.inhandItemCount);
            TestAssert.AreEqual(remainingInc, player.inhandItemInc);
            TestAssert.AreEqual(remainingCount == 0 ? 0 : 1601, player.inhandItemId);
            TestAssert.AreEqual(depositCount, requestedCount);
        }
        finally
        {
            harmony.UnpatchSelf();
            Multiplayer.Session = previousSession;
            StorageComponent.staticLoaded = previousLoaded;
            ItemProto.turretNeeds = previousNeeds;
        }
    }
}
