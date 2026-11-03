using System.Reflection;
using HarmonyLib;
using global::NebulaeModel.Authority;
using global::NebulaeModel.DataStructures;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Combat;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class WeaponSelectionRegressionTest
{
    private GameData oldGame = null!;
    private MultiplayerSession oldSession = null!;
    private bool oldLoaded;
    private int[] oldStacks = null!;
    private Harmony harmony = null!;
    private Mecha mecha = null!;
    private PlayerWeaponResources resources = null!;

    [TestInitialize]
    public void Setup()
    {
        oldGame = GameMain.data; oldSession = Multiplayer.Session;
        oldLoaded = StorageComponent.staticLoaded; oldStacks = StorageComponent.itemStackCount;
        StorageComponent.staticLoaded = true; StorageComponent.itemStackCount = Enumerable.Repeat(100, 12000).ToArray();
        harmony = new Harmony("nebulae.tests.weapon-selection");
        harmony.Patch(AccessTools.Method(typeof(Mecha), nameof(Mecha.LoadAmmo)),
            prefix: new HarmonyMethod(typeof(WeaponSelectionRegressionTest), nameof(LoadTestMagazine)));
        harmony.Patch(AccessTools.Method(typeof(CombatModuleComponent), "Setup", [typeof(int), typeof(GameData)]),
            prefix: new HarmonyMethod(typeof(WeaponSelectionRegressionTest), nameof(SkipAssets)));
        var game = Uninitialized<GameData>(); var player = Uninitialized<Player>();
        player.isAlive = true;
        mecha = new Mecha
        {
            player = player,
            hp = 100,
            ammoItemId = 1601,
            ammoBulletCount = 10,
            ammoSelectSlot = 1,
            laserActive = true,
            ammoStorage = new StorageComponent(3),
            bombStorage = new StorageComponent(1),
            fighterStorage = new StorageComponent(5),
            groundCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] },
            spaceCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] }
        };
        mecha.ammoStorage.grids[0] = new StorageComponent.GRID { itemId = 1601, count = 5, stackSize = 100 };
        mecha.ammoStorage.grids[1] = new StorageComponent.GRID { itemId = 1606, count = 5, stackSize = 100 };
        SetBackingField(player, nameof(Player.mecha), mecha);
        SetBackingField(player, nameof(Player.package), new StorageComponent(2));
        SetBackingField(game, nameof(GameData.mainPlayer), player);
        GameMain.data = game;
        var session = Uninitialized<MultiplayerSession>();
        session.Client = Uninitialized<global::NebulaeNetwork.Client>();
        session.LocalPlayer = new LocalPlayer { Data = new PlayerData(2, 101) };
        session.Authority = new AuthoritySessionState();
        session.Authority.BeginAuthorityWorld(new AuthorityEpoch(1, 2), false);
        session.PlayerWeapons = new PlayerWeaponManager();
        Multiplayer.Session = session;
        resources = session.PlayerWeapons.Resources;
    }

    [TestCleanup]
    public void Cleanup()
    {
        harmony.UnpatchSelf(); resources.Clear(); PlayerWeaponResources.Active = false;
        GameMain.data = oldGame; Multiplayer.Session = oldSession;
        StorageComponent.staticLoaded = oldLoaded; StorageComponent.itemStackCount = oldStacks;
    }

    private PlayerShotId Fire(long sequence, int slot)
    {
        mecha.ammoSelectSlot = slot;
        var shot = new PlayerShotId(2, 1, sequence);
        using (resources.Enter(mecha))
        {
            TestAssert.AreEqual(slot, mecha.ammoSelectSlot, "Old shot replay must not select the weapon for the next callback.");
            if (slot == 2 && mecha.ammoItemId != 1606) mecha.LoadAmmo(false);
            resources.Reserve(shot, slot == 2 ? EAmmoType.Missile : EAmmoType.Bullet, false, mecha.ammoItemId, slot);
            PlayerWeaponResources.ConsumeRound(mecha);
        }
        return shot;
    }

    [TestMethod]
    public void SwitchingWhileShotsAreUnconfirmedKeepsSelectionAndDisplaysTheNewMagazine()
    {
        var ammo = mecha.ammoStorage;
        Fire(1, 1);
        TestAssert.AreEqual(9, PlayerWeaponResources.DisplayAmmoBullets(mecha));
        var second = Fire(2, 2);
        TestAssert.AreEqual(2, mecha.ammoSelectSlot);
        TestAssert.AreEqual(1606, PlayerWeaponResources.DisplayAmmoItem(mecha));
        TestAssert.AreEqual(4, PlayerWeaponResources.DisplayAmmoBullets(mecha));
        TestAssert.AreEqual(1601, mecha.ammoItemId, "The displayed magazine must not become checkpoint state.");
        TestAssert.AreEqual(10, mecha.ammoBulletCount);
        TestAssert.AreSame(ammo, mecha.ammoStorage);
        TestAssert.AreEqual(5, ammo.grids[1].count, "Prediction cannot spend the real magazine inventory.");
        resources.Reject(second);
        TestAssert.AreEqual(1601, PlayerWeaponResources.DisplayAmmoItem(mecha));
        TestAssert.AreEqual(9, PlayerWeaponResources.DisplayAmmoBullets(mecha));
        TestAssert.AreEqual(2, mecha.ammoSelectSlot, "Rejection refunds resources without undoing the user's current selection.");
    }

    [TestMethod]
    public void CheckpointAndSettlementDoNotDoubleSpendTheDisplayedRounds()
    {
        var shot = Fire(1, 1);
        mecha.ammoBulletCount = 9;
        resources.Settle(shot, 8); resources.Applied(8); resources.Rebase(mecha, 100);
        TestAssert.AreEqual(9, PlayerWeaponResources.DisplayAmmoBullets(mecha));
        var second = Fire(2, 1);
        TestAssert.AreEqual(8, PlayerWeaponResources.DisplayAmmoBullets(mecha));
        resources.Reject(second);
        TestAssert.AreEqual(9, PlayerWeaponResources.DisplayAmmoBullets(mecha));
    }

    [TestMethod]
    public void ReplayedShotsCannotReactivateAnExplicitlyDisabledAmmoSlot()
    {
        Fire(1, 1);
        mecha.ammoSelectSlot = 0;
        using (resources.Enter(mecha)) TestAssert.AreEqual(0, mecha.ammoSelectSlot);
        TestAssert.AreEqual(0, mecha.ammoSelectSlot);
    }

    [TestMethod]
    public void MagazineDisplayDropsPredictionsFromThePreviousLife()
    {
        Fire(1, 1);
        TestAssert.AreEqual(9, PlayerWeaponResources.DisplayAmmoBullets(mecha));
        mecha.player.deathCount++;
        TestAssert.AreEqual(10, PlayerWeaponResources.DisplayAmmoBullets(mecha));
    }

    [TestMethod]
    public void WeaponPanelReadsPredictedAmmoWithoutRedirectingItsSelectionFields()
    {
        var refresh = AccessTools.Method(typeof(UIZS_WeaponPanel), "RefreshPanelCompletely");
        var code = CombatPredictionView_Patch.Transpiler(PatchProcessor.GetOriginalInstructions(refresh)).ToArray();
        foreach (var name in new[] { nameof(Mecha.ammoItemId), nameof(Mecha.ammoBulletCount), nameof(Mecha.ammoInc) })
            TestAssert.IsFalse(code.Any(x => x.LoadsField(AccessTools.Field(typeof(Mecha), name))));
        TestAssert.IsTrue(code.Any(x => x.Calls(AccessTools.Method(typeof(PlayerWeaponResources), nameof(PlayerWeaponResources.DisplayAmmoItem)))));
        TestAssert.IsTrue(code.Any(x => x.LoadsField(AccessTools.Field(typeof(Mecha), nameof(Mecha.ammoSelectSlot)))));
    }

    [TestMethod]
    public void AllNativeModesSurviveSettingsEncodingWithoutChangingOtherFlags()
    {
        const int flags = 0x401FFFFF;
        for (var ammo = 0; ammo < 3; ammo++)
            for (var laser = 0; laser < 3; laser++)
                foreach (var bomb in new[] { false, true })
                {
                    var encoded = CombatWeaponSettings.Encode(flags, ammo, laser, bomb);
                    TestAssert.AreEqual(flags, encoded & ~CombatWeaponSettings.Mask);
                    TestAssert.IsTrue(CombatWeaponSettings.TryDecode(encoded, out var a, out var l, out var b));
                    TestAssert.AreEqual(ammo, a); TestAssert.AreEqual(laser, l); TestAssert.AreEqual(bomb, b);
                    TestAssert.IsTrue(CombatIntent.TryDecode(new CombatIntent(CombatIntentKind.Settings, encoded).Encode(), out _));
                }
        TestAssert.IsFalse(CombatWeaponSettings.TryDecode(3 << 21, out _, out _, out _));
        TestAssert.IsFalse(CombatWeaponSettings.TryDecode(3 << 23, out _, out _, out _));
    }

    [TestMethod]
    public void NativeAmmoAndLaserManualSwitchesSurviveFightSnapshots()
    {
        var panel = Uninitialized<UIZS_WeaponPanel>(); SetField(panel, "mecha", mecha);
        mecha.ammoSelectSlotState = 0; mecha.laserActiveState = 1;
        panel.OnLaserActiveManualButtonClick(0);
        var copy = RoundTripFight(new MechaFightData(mecha.player));
        TestAssert.AreEqual(1, copy.AmmoSelectSlotState); TestAssert.AreEqual(0, copy.LaserActiveState);
        var host = Uninitialized<Player>(); var hostMecha = new Mecha(); SetBackingField(host, nameof(Player.mecha), hostMecha);
        copy.UpdateMech(host);
        TestAssert.IsFalse(CombatWeaponSettings.Allows(hostMecha, false, true));
        TestAssert.IsTrue(CombatWeaponSettings.Allows(hostMecha, false, false));
        TestAssert.IsTrue(CombatWeaponSettings.Allows(hostMecha, true, true));
        panel.OnAmmoActiveManualButtonClick(0);
        copy = RoundTripFight(new MechaFightData(mecha.player)); copy.UpdateMech(host);
        TestAssert.IsTrue(CombatWeaponSettings.Allows(hostMecha, false, true));
        TestAssert.IsFalse(CombatWeaponSettings.Allows(hostMecha, true, true));
        panel.OnBombActiveButtonClick(0);
        copy = RoundTripFight(new MechaFightData(mecha.player));
        TestAssert.IsTrue(copy.BombActive);
        TestAssert.AreEqual(1, copy.AmmoSelectSlotState); TestAssert.AreEqual(1, copy.LaserActiveState);
    }

    [TestMethod]
    public void SwitchingToAutoOnlyCancelsQueuedManualShotsButKeepsAutoShots()
    {
        var manual = Fire(1, 1); var automatic = Fire(2, 1);
        var queue = new Queue<(ObjectKey, EAmmoType, long, PlayerShotId, bool, int, int)>();
        queue.Enqueue((default, EAmmoType.Bullet, 0, manual, true, 1601, 1));
        queue.Enqueue((default, EAmmoType.Bullet, 0, automatic, false, 1601, 1));
        mecha.ammoSelectSlotState = 1;
        AccessTools.Method(typeof(CombatAuthorityManager), "CancelDisallowedShots").Invoke(null, [queue, mecha, false]);
        TestAssert.HasCount(1, queue); TestAssert.AreEqual(automatic, queue.Peek().Item4);
        TestAssert.IsFalse(resources.Contains(manual)); TestAssert.IsTrue(resources.Contains(automatic));
    }

    [TestMethod]
    public void HostFinishesAnOlderShotWithoutOverwritingTheNewSelectedSlot()
    {
        harmony.Patch(AccessTools.Method(typeof(CombatAuthorityManager), "ValidWeaponTarget"),
            prefix: new HarmonyMethod(typeof(WeaponSelectionRegressionTest), nameof(ValidTarget)));
        harmony.Patch(AccessTools.Method(typeof(CombatAuthorityManager), "WithinWeaponRange"),
            prefix: new HarmonyMethod(typeof(WeaponSelectionRegressionTest), nameof(ValidTarget)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Mecha), nameof(Mecha.activeAmmoType)),
            prefix: new HarmonyMethod(typeof(WeaponSelectionRegressionTest), nameof(BulletAmmo)));
        harmony.Patch(AccessTools.Method(typeof(PlayerAction_Combat), nameof(PlayerAction_Combat.ShootTarget)),
            prefix: new HarmonyMethod(typeof(WeaponSelectionRegressionTest), nameof(RecordShotSlot)));
        var manager = new CombatAuthorityManager { TargetWorld = new TargetWorld() };
        var actorType = typeof(CombatAuthorityManager).GetNestedType("Actor", BindingFlags.NonPublic)!;
        var actor = Activator.CreateInstance(actorType, true)!;
        var action = Uninitialized<PlayerAction_Combat>(); SetField(action, "mecha", mecha);
        actorType.GetField("Player")!.SetValue(actor, mecha.player);
        actorType.GetField("Mecha")!.SetValue(actor, mecha);
        actorType.GetField("Data")!.SetValue(actor, new PlayerData(2, 101));
        actorType.GetField("Action")!.SetValue(actor, action);
        var queue = (Queue<(ObjectKey, EAmmoType, long, PlayerShotId, bool, int, int)>)actorType.GetField("AmmoIntents")!.GetValue(actor)!;
        queue.Enqueue((ObjectKey.Create(new AuthorityEpoch(1, 2), PoolKind.GroundEnemy, 101, 7, 1),
            EAmmoType.Bullet, 0, new PlayerShotId(2, 1, 1), true, 1601, 1));
        mecha.ammoSelectSlot = 2;
        slotAtShot = 0;
        var target = (SkillTarget)AccessTools.Method(typeof(CombatAuthorityManager), "FirePending").Invoke(manager, [actor, false])!;
        TestAssert.AreEqual(7, target.id);
        TestAssert.AreEqual(1, slotAtShot, "The accepted old shot still owns its original magazine.");
        TestAssert.AreEqual(2, mecha.ammoSelectSlot, "Finishing that shot must restore the newer user selection.");
        TestAssert.AreEqual(9, mecha.ammoBulletCount);
    }

    [TestMethod]
    public void OldFightBlobsWithoutModeExtensionRemainReadable()
    {
        var writer = new NetDataWriter(); new MechaFightData(mecha.player).Serialize(writer);
        var bytes = writer.CopyData();
        // The existing fixed scalar prefix is 61 bytes, followed by the native blob's length.
        var legacy = bytes.Take(bytes.Length - 8).ToArray();
        Buffer.BlockCopy(BitConverter.GetBytes(BitConverter.ToInt32(bytes, 61) - 8), 0, legacy, 61, 4);
        var copy = new MechaFightData(); copy.Deserialize(new NetDataReader(legacy));
        TestAssert.AreEqual(1601, copy.AmmoItemId); TestAssert.AreEqual(0, copy.AmmoSelectSlotState);
        TestAssert.AreEqual(0, copy.LaserActiveState); TestAssert.IsFalse(copy.BombActive);
    }

    [TestMethod]
    public void ModeChangesAreSentBeforePredictionWithoutFloodingEnergyOnlyReports()
    {
        // Patch the implementation; the two-argument forwarding overload may already
        // have been inlined by earlier fleet tests in the same test host.
        harmony.Patch(AccessTools.Method(typeof(CombatAuthorityManager), nameof(CombatAuthorityManager.Send), [typeof(CombatIntent), typeof(SkillTarget), typeof(long).MakeByRefType()]),
            prefix: new HarmonyMethod(typeof(WeaponSelectionRegressionTest), nameof(CaptureSettings)));
        var session = Multiplayer.Session;
        session.IsGameLoaded = true;
        session.CombatAuthority = new CombatAuthorityManager();
        mecha.groundCombatModule.moduleFleets = new ModuleFleet[1];
        sentSettings.Clear(); settingsDuringPrediction = false;
        session.CombatAuthority.SendSettings(100);
        mecha.ammoSelectSlotState = 2;
        session.CombatAuthority.SendSettings(101, immediate: true);
        TestAssert.HasCount(2, sentSettings);
        TestAssert.IsTrue(CombatWeaponSettings.TryDecode(sentSettings[1].Argument, out var mode, out _, out _));
        TestAssert.AreEqual(2, mode);
        mecha.coreEnergy = 100;
        session.CombatAuthority.SendSettings(102, immediate: true);
        TestAssert.HasCount(2, sentSettings, "An unchanged configuration still uses the normal energy report cadence.");
        mecha.laserActiveState = 1;
        PlayerCombatAuthority_Patch.FireClock_Prefix(mecha, out var scope);
        try
        {
            TestAssert.HasCount(3, sentSettings);
            TestAssert.IsFalse(settingsDuringPrediction, "Settings must contain real resources, before entering the disposable prediction scope.");
        }
        finally { scope?.Dispose(); }
    }

    private static MechaFightData RoundTripFight(MechaFightData fight)
    {
        var writer = new NetDataWriter(); fight.Serialize(writer);
        var copy = new MechaFightData(); copy.Deserialize(new NetDataReader(writer.CopyData())); return copy;
    }

    private static bool SkipAssets() => false;
    private static readonly List<CombatIntent> sentSettings = new();
    private static bool settingsDuringPrediction;
    private static bool CaptureSettings(CombatIntent intent, ref bool __result)
    {
        if (intent.Kind == CombatIntentKind.Settings)
        {
            sentSettings.Add(intent);
            settingsDuringPrediction |= PlayerWeaponResources.Active;
        }
        __result = true; return false;
    }
    private static int slotAtShot;
    private static bool ValidTarget(ref bool __result) { __result = true; return false; }
    private static bool BulletAmmo(ref EAmmoType __result) { __result = EAmmoType.Bullet; return false; }
    private static bool RecordShotSlot(PlayerAction_Combat __instance, ref bool __result)
    {
        slotAtShot = ((Mecha)AccessTools.Field(typeof(PlayerAction_Combat), "mecha").GetValue(__instance)).ammoSelectSlot;
        __result = true; return false;
    }
    private sealed class TargetWorld : IHostWorldView
    {
        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members) => true;
        public bool TryReadState(ObjectKey key, out byte[] state) { state = [1]; return true; }
    }
    private static bool LoadTestMagazine(Mecha __instance)
    {
        if (__instance.ammoSelectSlot <= 0) return false;
        var item = 0; var count = 1;
        __instance.ammoStorage.TakeItemFromGrid(__instance.ammoSelectSlot - 1, ref item, ref count, out var inc);
        if (count > 0) { __instance.ammoItemId = item; __instance.ammoInc = inc; __instance.ammoBulletCount = 5; }
        return false;
    }
}
