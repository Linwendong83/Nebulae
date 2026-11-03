using System.Runtime.Serialization;
using global::NebulaeModel.Authority;
using global::NebulaeModel.DataStructures;
using global::NebulaeModel.Packets.Authority;
using global::NebulaeModel.Packets.Combat;
using global::NebulaeModel.Utils;
using NebulaeWorld.Combat;
using NebulaeWorld;
using NebulaeWorld.Authority.Adapters;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class PlayerWeaponPredictionTest
{
    [TestMethod]
    [DataRow(EAmmoType.Bullet, true)]
    [DataRow(EAmmoType.Laser, true)]
    [DataRow(EAmmoType.Cannon, true)]
    [DataRow(EAmmoType.Plasma, true)]
    [DataRow(EAmmoType.Missile, true)]
    [DataRow(EAmmoType.None, false)]
    [DataRow(EAmmoType.EMCapsule, false)]
    [DataRow((EAmmoType)255, false)]
    public void EveryNativeMechaWeaponPassesAdmissionRegardlessOfEnumOrdering(EAmmoType ammo, bool allowed)
    {
        var data = new CombatIntent(CombatIntentKind.ManualShoot, (int)ammo, 1601, 1).Encode();
        TestAssert.IsTrue(CombatIntent.TryDecode(data, out var intent));
        TestAssert.AreEqual(allowed, PlayerWeaponTargets.IsMechaAmmo((EAmmoType)intent.Argument));
    }

    [TestMethod]
    public void RemotePlayerHitsOnAClientUseThePresentationTableInsteadOfHostActors()
    {
        var oldSession = Multiplayer.Session; var oldGame = GameMain.data; var dedicated = Multiplayer.IsDedicated;
        try
        {
            var game = Uninitialized<GameData>();
            var player = Uninitialized<Player>();
            var local = new Mecha(); var remote = new Mecha();
            player.SetHiddenProperty(nameof(Player.mecha), local); game.SetHiddenProperty(nameof(GameData.mainPlayer), player);
            var session = Uninitialized<MultiplayerSession>();
            session.SetHiddenProperty(nameof(MultiplayerSession.Client), FormatterServices.GetUninitializedObject(typeof(global::NebulaeNetwork.Client)));
            session.LocalPlayer = new LocalPlayer { Data = new global::NebulaeModel.DataStructures.PlayerData(2, 101) };
            session.CombatAuthority = new CombatAuthorityManager(); session.Combat = new CombatManager();
            session.Combat.Players = [new CombatManager.PlayerPosition { id = 2, mecha = local }, new CombatManager.PlayerPosition { id = 3, mecha = remote }];
            session.Combat.IndexByPlayerId[2] = 0; session.Combat.IndexByPlayerId[3] = 1;
            GameMain.data = game; Multiplayer.Session = session; Multiplayer.IsDedicated = false;
            TestAssert.IsTrue(CombatTargetContext.IsPlayerTarget(3));
            TestAssert.AreSame(remote, CombatTargetContext.ResolveDamageMecha(null!, 3));
            TestAssert.AreEqual(3, CombatTargetContext.PlayerIdFor(remote));
            TestAssert.IsNull(CombatTargetContext.ResolveDamageMecha(null!, 4));
        }
        finally { Multiplayer.Session = oldSession; GameMain.data = oldGame; Multiplayer.IsDedicated = dedicated; }
    }

    private bool loaded;
    private int[] stacks = null!;
    [TestInitialize]
    public void Setup()
    {
        loaded = StorageComponent.staticLoaded; stacks = StorageComponent.itemStackCount;
        StorageComponent.staticLoaded = true; StorageComponent.itemStackCount = Enumerable.Repeat(100, 12000).ToArray();
    }
    [TestCleanup]
    public void Cleanup()
    {
        StorageComponent.staticLoaded = loaded; StorageComponent.itemStackCount = stacks;
        PlayerWeaponResources.Active = false;
    }

    [TestMethod]
    public void ShotIdentitySurvivesRoundTripAndSeparatesConnectionsAndParts()
    {
        var epoch = new AuthorityEpoch(1, 2);
        var target = ObjectKey.Create(epoch, PoolKind.GroundEnemy, 101, 5, 7);
        var samples = new[]
        {
            new PlayerWeaponSample { Shot = new PlayerShotId(2, 3, 4), Part = 1, Event = PlayerWeaponEvent.Launch,
                Kind = BattleEffectKind.MechaGroundGauss, Tick = 20, Star = 1, Astro = 101, Target = target, Payload = [1, 2] },
            new PlayerWeaponSample { Shot = new PlayerShotId(2, 3, 4), Part = 1, Ordinal = 2, Event = PlayerWeaponEvent.Impact,
                Kind = BattleEffectKind.MechaGroundGauss, Tick = 21, Subtype = 20, Payload = [3] }
        };
        var bytes = PlayerWeaponSample.Encode(samples);
        var copy = PlayerWeaponSample.Decode(bytes);
        TestAssert.AreEqual(target, copy[0].Target); TestAssert.AreEqual(samples[0].Shot, copy[0].Shot);
        TestAssert.AreEqual(1, copy[1].Part); TestAssert.AreEqual(2, copy[1].Ordinal);
        TestAssert.AreNotEqual(copy[0].Shot, new PlayerShotId(2, 4, 4));
        TestAssert.ThrowsExactly<InvalidDataException>(() => PlayerWeaponSample.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
        TestAssert.ThrowsExactly<EndOfStreamException>(() => PlayerWeaponSample.Decode(bytes.Take(30).ToArray()));
    }

    [TestMethod]
    public void SettlementWaitsForTheActualCombatRevisionInEitherDeliveryOrder()
    {
        var resources = new PlayerWeaponResources();
        var first = new PlayerShotId(2, 1, 10); var second = new PlayerShotId(2, 1, 11);
        resources.Reserve(first, EAmmoType.Bullet, false, 0);
        resources.Reserve(second, EAmmoType.Bullet, false, 0);
        resources.Settle(first, 8); resources.Applied(7);
        TestAssert.AreEqual(2, resources.PendingCount);
        resources.Applied(8); TestAssert.AreEqual(1, resources.PendingCount);
        resources.Settle(first, 8); TestAssert.AreEqual(1, resources.PendingCount);
        resources.Applied(9); resources.Settle(second, 9);
        TestAssert.AreEqual(0, resources.PendingCount);
    }

    [TestMethod]
    public void PredictionConsumesTheLastBulletOnlyInTheShadowAndRejectionRefundsIt()
    {
        var player = Uninitialized<Player>();
        var mecha = new Mecha
        {
            player = player,
            ammoStorage = new StorageComponent(1),
            bombStorage = new StorageComponent(1),
            ammoBulletCount = 1,
            coreEnergy = 100,
            laserEnergy = 100
        };
        var package = new StorageComponent(2); player.SetHiddenProperty(nameof(Player.package), package);
        var resources = new PlayerWeaponResources(); var shot = new PlayerShotId(2, 1, 1);
        using (resources.Enter(mecha))
        {
            TestAssert.AreNotSame(package, player.package);
            resources.Reserve(shot, EAmmoType.Bullet, false, 0);
            mecha.ammoBulletCount--; mecha.coreEnergy -= 10;
            player.package.grids[0].count = 99;
        }
        TestAssert.AreSame(package, player.package); TestAssert.AreEqual(0, package.grids[0].count);
        TestAssert.AreEqual(1, mecha.ammoBulletCount); TestAssert.AreEqual(100d, mecha.coreEnergy);
        using (resources.Enter(mecha)) { TestAssert.AreEqual(0, mecha.ammoBulletCount); TestAssert.AreEqual(90d, mecha.coreEnergy); }
        resources.Reject(shot);
        using (resources.Enter(mecha)) TestAssert.AreEqual(1, mecha.ammoBulletCount);
        TestAssert.IsFalse(PlayerWeaponResources.Active);
    }

    [TestMethod]
    public void ImportedNativeProjectileKeepsCollisionAndEffectParametersButNotHostStatSlots()
    {
        var skills = Uninitialized<SkillSystem>();
        skills.mechaLocalGaussProjectiles = new DataPoolRenderer<LocalGeneralProjectile>(); skills.mechaLocalGaussProjectiles.ResetPool();
        var pool = NativePlayerWeaponPool.Create(skills).Single(x => x.Kind == BattleEffectKind.MechaGroundGauss);
        var native = new LocalGeneralProjectile
        {
            id = 93,
            astroId = 101,
            life = 36,
            damage = 1200,
            mask = ETargetTypeMask.Enemy,
            targetCombatStatId = 56,
            damageIncoming = 1200,
            pos = new Vector3(1, 2, 3)
        };
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); native.Export(writer);
        var id = pool.Install(stream.ToArray()); var restored = (LocalGeneralProjectile)pool.Read(id);
        TestAssert.AreEqual(1, id); TestAssert.AreEqual(1200, restored.damage); TestAssert.AreEqual(ETargetTypeMask.Enemy, restored.mask);
        TestAssert.AreEqual(0, restored.targetCombatStatId); TestAssert.AreEqual(0, restored.damageIncoming);
        TestAssert.AreEqual(3f, restored.pos.z);
        pool.Remove(id); TestAssert.IsFalse(pool.Exists(id));
    }

    [TestMethod]
    public void ShieldBurstReservesEnergyOnlyInTheViewAndRejectionReleasesIt()
    {
        var player = Uninitialized<Player>();
        var mecha = new Mecha
        {
            player = player,
            ammoStorage = new StorageComponent(1),
            bombStorage = new StorageComponent(1),
            energyShieldCapacity = 1000,
            energyShieldEnergy = 1000,
            energyShieldBurstProgress = .4,
            coreEnergy = 100
        };
        player.SetHiddenProperty(nameof(Player.package), new StorageComponent(1));
        var resources = new PlayerWeaponResources(); var shot = new PlayerShotId(2, 1, 1);
        using (resources.Enter(mecha))
        {
            resources.Reserve(shot, EAmmoType.None, false, 0);
            mecha.ShieldBurstComsumeEnergy();
            TestAssert.AreEqual(600L, mecha.energyShieldEnergy);
        }
        TestAssert.AreEqual(1000L, mecha.energyShieldEnergy);
        using (resources.Enter(mecha)) TestAssert.AreEqual(600L, mecha.energyShieldEnergy);
        TestAssert.AreEqual(1000L, mecha.energyShieldEnergy);
        resources.Reject(shot);
        using (resources.Enter(mecha)) TestAssert.AreEqual(1000L, mecha.energyShieldEnergy);
        TestAssert.AreEqual(0, resources.PendingCount);
    }

    [TestMethod]
    public void TurretSupplyStateIsIndependentOfItsAimingAnimation()
    {
        var state = new GroundEnemyState(GroundEnemyKind.GroundTurret, false, false,
            8120, 292, 1, 0, 0, 101, 101, 1, 1, 2,
            0, 200, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
            animationState: 2, turretState: 2, builderState: 4);
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(state, out var bytes));
        TestAssert.IsTrue(GroundEnemyStateCodec.TryDecode(bytes, 0, bytes.Length, out var copy, out _));
        TestAssert.AreEqual(4, copy.BuilderState); TestAssert.AreEqual(2u, copy.AnimationState);
        var info = new EnemyBriefInfo();
        var patch = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Authority.EnemyBriefPresentation_Patch", true)!;
        patch.GetMethod("Apply", [typeof(EnemyBriefInfo), typeof(GroundEnemyState)])!.Invoke(null, [info, copy]);
        TestAssert.AreEqual(4, info.builderState); TestAssert.AreEqual(2, info.turretState);
    }

    [TestMethod]
    public void NativeModelHealthBarUsesItsWidthAndRadialHeight()
    {
        var oldHeight = SkillSystem.BarHeightByModelIndex; var oldWidth = SkillSystem.BarWidthByModelIndex;
        try
        {
            SkillSystem.BarHeightByModelIndex = [0, 12]; SkillSystem.BarWidthByModelIndex = [0, 8];
            var enemy = new EnemyData { modelIndex = 1, pos = new VectorLF3(0, 200, 0) }; var stat = new CombatStat();
            EnemyHealthBar.Update(ref stat, enemy, false);
            TestAssert.AreEqual(212f, stat.localPos.y); TestAssert.AreEqual(8f, stat.size);
        }
        finally { SkillSystem.BarHeightByModelIndex = oldHeight; SkillSystem.BarWidthByModelIndex = oldWidth; }
    }
}
