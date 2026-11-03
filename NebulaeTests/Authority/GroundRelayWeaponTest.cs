using HarmonyLib;
using NebulaeWorld.Combat;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class GroundRelayWeaponTest
{
    private Harmony positionPatch = null!;
    private static Vector3 playerPosition;

    [TestInitialize]
    public void InstallPositionBoundary()
    {
        positionPatch = new Harmony("nebulae.tests.ground-relay-position");
        positionPatch.Patch(AccessTools.PropertyGetter(typeof(Player), nameof(Player.position)),
            prefix: new HarmonyMethod(typeof(GroundRelayWeaponTest), nameof(GetPosition)));
        positionPatch.Patch(AccessTools.PropertySetter(typeof(Player), nameof(Player.position)),
            prefix: new HarmonyMethod(typeof(GroundRelayWeaponTest), nameof(SetPosition)));
    }

    [TestCleanup]
    public void RestorePositionBoundary() => positionPatch.UnpatchSelf();

    private static (Player Player, PlanetFactory Factory, EnemyData Enemy) GroundRelay()
    {
        var factory = Uninitialized<PlanetFactory>();
        var planet = new PlanetData { id = 101, radius = 200, factory = factory };
        SetBackingField(factory, nameof(PlanetFactory.planet), planet);
        factory.enemySystem = Uninitialized<EnemyDFGroundSystem>();
        factory.enemySystem.bases = new ObjectPool<DFGBaseComponent>();
        factory.enemySystem.bases.Reset();
        var component = factory.enemySystem.bases.Add();
        component.relayEnemyId = 7;
        var player = Uninitialized<Player>();
        player.position = new Vector3(0, 200, 0);
        player.movementState = EMovementState.Walk;
        var enemy = new EnemyData { id = 7, dfRelayId = 2, astroId = 101, originAstroId = 1000001, isSpace = true };
        return (player, factory, enemy);
    }

    [TestMethod]
    [DataRow(EMovementState.Walk)]
    [DataRow(EMovementState.Drift)]
    [DataRow(EMovementState.Fly)]
    public void LocalBaseRelayCanBeManuallyAttackedWithoutSailing(EMovementState movement)
    {
        var (player, factory, enemy) = GroundRelay();
        player.movementState = movement;
        TestAssert.IsTrue(PlayerWeaponTargets.CanAttackGroundRelay(player, factory, enemy, true));
        TestAssert.AreEqual(1000001, enemy.originAstroId, "Target identity stays in the sector pool.");
        TestAssert.IsTrue(enemy.isSpace);
        TestAssert.IsFalse(PlayerWeaponTargets.CanAttackGroundRelay(player, factory, enemy, false), "Vanilla does not acquire relays automatically from the ground.");
    }

    [TestMethod]
    public void GroundExceptionRequiresTheSamePlanetsLiveBaseLink()
    {
        var (player, factory, enemy) = GroundRelay();
        TestAssert.IsFalse(PlayerWeaponTargets.CanAttackGroundRelay(player, null!, enemy, true));
        enemy.astroId = 102;
        TestAssert.IsFalse(PlayerWeaponTargets.CanAttackGroundRelay(player, factory, enemy, true));
        enemy.astroId = 101; enemy.dfRelayId = 0;
        TestAssert.IsFalse(PlayerWeaponTargets.CanAttackGroundRelay(player, factory, enemy, true), "Ordinary space enemies cannot use the relay exception.");
        enemy.dfRelayId = 2;
        factory.enemySystem.bases.buffer[1].relayEnemyId = 8;
        TestAssert.IsFalse(PlayerWeaponTargets.CanAttackGroundRelay(player, factory, enemy, true));
        factory.enemySystem.bases.buffer[1].relayEnemyId = 7;
        factory.enemySystem.bases.buffer[1].id = 0;
        TestAssert.IsFalse(PlayerWeaponTargets.CanAttackGroundRelay(player, factory, enemy, true), "A recycled base slot cannot authorize a shot.");
    }

    [TestMethod]
    public void GroundRelayRetainsNativeInvincibilityWarpAndAltitudeLimits()
    {
        var (player, factory, enemy) = GroundRelay();
        enemy.isInvincible = true;
        TestAssert.IsFalse(PlayerWeaponTargets.CanAttackGroundRelay(player, factory, enemy, true));
        enemy.isInvincible = false; player.warpCommand = true;
        TestAssert.IsFalse(PlayerWeaponTargets.CanAttackGroundRelay(player, factory, enemy, true));
        player.warpCommand = false; player.position = new Vector3(0, 350, 0);
        TestAssert.IsFalse(PlayerWeaponTargets.CanAttackGroundRelay(player, factory, enemy, true));
        player.position = new Vector3(0, 349, 0);
        TestAssert.IsTrue(PlayerWeaponTargets.CanAttackGroundRelay(player, factory, enemy, true));
        player.movementState = EMovementState.Sail;
        TestAssert.IsFalse(PlayerWeaponTargets.CanAttackGroundRelay(player, factory, enemy, true), "Sailing continues to use the space targeting rules.");
    }

    [TestMethod]
    public void RelayTravelAndBaseRemovalInvalidateAnAlreadyAcceptedGroundTarget()
    {
        var (player, factory, enemy) = GroundRelay();
        TestAssert.IsTrue(PlayerWeaponTargets.CanAttackGroundRelay(player, factory, enemy, true));
        enemy.astroId = 100;
        TestAssert.IsFalse(PlayerWeaponTargets.CanAttackGroundRelay(player, factory, enemy, true));
        enemy.astroId = 101;
        factory.enemySystem.bases.buffer[1] = null;
        TestAssert.IsFalse(PlayerWeaponTargets.CanAttackGroundRelay(player, factory, enemy, true));
    }

    [TestMethod]
    [DataRow(EAmmoType.Missile)]
    [DataRow(EAmmoType.Bullet)]
    [DataRow(EAmmoType.Cannon)]
    [DataRow(EAmmoType.Plasma)]
    [DataRow(EAmmoType.Laser)]
    public void GroundRelayUsesNativeLocalRangeAndTheMidpointOfBothMuzzles(EAmmoType ammo)
    {
        var (player, _, enemy) = GroundRelay();
        var mecha = new Mecha
        {
            player = player,
            bulletLocalAttackRange = 10,
            bulletSpaceAttackRange = 1000,
            cannonLocalAttackRange = 10,
            cannonSpaceAttackRange = 1000,
            plasmaLocalAttackRange = 10,
            plasmaSpaceAttackRange = 1000,
            missileLocalAttackRange = 10,
            missileSpaceAttackRange = 1000,
            laserLocalAttackRange = 10,
            laserSpaceAttackRange = 1000
        };
        var action = Uninitialized<PlayerAction_Combat>();
        SetField(action, "mecha", mecha);
        var harmony = new Harmony("nebulae.tests.ground-relay-range");
        try
        {
            // Supply only the Unity bone positions. The native ammo-range getter stays intact.
            harmony.Patch(AccessTools.PropertyGetter(typeof(Mecha), nameof(Mecha.skillCastLeftL)),
                prefix: new HarmonyMethod(typeof(GroundRelayWeaponTest), nameof(LeftMuzzle)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Mecha), nameof(Mecha.skillCastRightL)),
                prefix: new HarmonyMethod(typeof(GroundRelayWeaponTest), nameof(RightMuzzle)));
            enemy.pos = new VectorLF3(31, 200, 0); // 29 from the muzzle midpoint at x=2.
            TestAssert.IsTrue(PlayerWeaponTargets.GroundRelayInRange(action, mecha, enemy, ammo, true));
            enemy.pos.x = 33;
            TestAssert.IsFalse(PlayerWeaponTargets.GroundRelayInRange(action, mecha, enemy, ammo, true), "Space range must not widen a ground manual attack.");
            enemy.pos.x = 11;
            TestAssert.IsTrue(PlayerWeaponTargets.GroundRelayInRange(action, mecha, enemy, ammo, false));
            enemy.pos.x = 13;
            TestAssert.IsFalse(PlayerWeaponTargets.GroundRelayInRange(action, mecha, enemy, ammo, false));
            mecha.missileLocalAttackRange = 0;
            enemy.pos.x = 2;
            TestAssert.IsFalse(PlayerWeaponTargets.GroundRelayInRange(action, mecha, enemy, EAmmoType.Missile, true), "A zero local range cannot be enabled by a nonzero space range.");
        }
        finally { harmony.UnpatchSelf(); }
    }

    private static bool LeftMuzzle(ref Vector3 __result) { __result = new Vector3(-4, 200, 0); return false; }
    private static bool RightMuzzle(ref Vector3 __result) { __result = new Vector3(8, 200, 0); return false; }
    private static bool GetPosition(ref Vector3 __result) { __result = playerPosition; return false; }
    private static bool SetPosition(Vector3 __0) { playerPosition = __0; return false; }
}
