using System.Reflection;
using HarmonyLib;
using global::NebulaeModel.DataStructures;
using NebulaeModel.Packets.Factory.PowerTower;
using NebulaeWorld;
using NebulaeWorld.Factory;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

/// <summary>Managed charging behavior using real game method bodies, without starting Unity or a network session.</summary>
[TestClass]
public class PowerTowerBehaviorTest
{
    private GameData previousGame = null!;
    private MultiplayerSession previousSession = null!;
    private bool initialized;
    private PowerSystem power = null!;
    private Player player = null!;
    private Mecha mecha = null!;
    private PowerTowerManager towers = null!;
    private Harmony? transformShim;
    private static Vector3 testPosition;

    [TestInitialize]
    public void Initialize()
    {
        TestAssert.IsGreaterThanOrEqualTo(100,
            typeof(PowerSystem).GetMethod("GameTick")!.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0, "Charging tests require the installed game's real method bodies.");
        previousGame = GameMain.data;
        previousSession = Multiplayer.Session;
        initialized = true;

        var game = Uninitialized<GameData>();
        var planet = Uninitialized<PlanetData>();
        planet.id = 101;
        planet.radius = 200f;
        planet.scale = 1f;
        player = Uninitialized<Player>();
        mecha = Uninitialized<Mecha>();
        mecha.coreEnergy = 100;
        mecha.coreEnergyCap = 1000;
        mecha.energyChanges = new double[20];
        mecha.chargerDevice = new int[256];
        player.isAlive = true;
        AccessTools.Field(typeof(Player), "_planetId").SetValue(player, 101);
        SetBackingField(player, "mecha", mecha);
        SetBackingField(game, "mainPlayer", player);
        SetBackingField(game, "localPlanet", planet);
        var factory = Uninitialized<PlanetFactory>();
        SetBackingField(factory, "gameData", game);
        SetBackingField(factory, "planet", planet);
        power = Uninitialized<PowerSystem>();
        power.factory = factory;
        power.nodeCursor = 2;
        power.nodePool = new PowerNodeComponent[2];
        power.nodePool[1] = new PowerNodeComponent
        {
            id = 1,
            entityId = 42,
            networkId = 1,
            isCharger = true,
            coverRadius = 8.7f,
            powerPoint = new Vector3(0, 200.2f / 0.988f, 0),
            idleEnergyPerTick = 10,
            workEnergyPerTick = 110,
            requiredEnergy = 110
        };
        power.networkServes = [0, 0.5f];
        var session = Uninitialized<MultiplayerSession>();
        var playerData = Uninitialized<PlayerData>();
        playerData.PlayerId = 1;
        session.LocalPlayer = new LocalPlayer { Data = playerData };
        towers = new PowerTowerManager();
        session.PowerTowers = towers;
        Multiplayer.Session = session;
        GameMain.data = game;
        // Substitute only the Unity Transform read; all charging rules and vector math are real.
        transformShim = new Harmony("nebulae.tests.player-position");
        transformShim.Patch(AccessTools.PropertyGetter(typeof(Player), "position"),
            prefix: new HarmonyMethod(typeof(PowerTowerBehaviorTest), nameof(GetTestPosition)));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (!initialized) return;
        towers?.Dispose();
        transformShim?.UnpatchSelf();
        GameMain.data = previousGame;
        Multiplayer.Session = previousSession;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ChargingUsesCorrectPositionRangeAltitudeAndPlayerState(bool multithreaded)
    {
        var near = new Vector3(0, 200.2f, 0);
        var far = new Vector3(200.2f, 0, 0);
        testPosition = multithreaded ? far : near;
        power.multithreadPlayerPos = multithreaded ? near : far;
        towers.UpdateLocalState(power, multithreaded);
        TestAssert.IsTrue(towers.IsLocalCharging(101, 1));
        towers.ApplyRemoteState(new PowerTowerChargerUpdate(2, 101, [1]));
        towers.ApplyRemoteState(new PowerTowerChargerUpdate(3, 102, [1]));
        TestAssert.AreEqual(2, towers.GetChargerCount(101, 1));

        mecha.coreEnergy = mecha.coreEnergyCap;
        towers.UpdateLocalState(power, multithreaded);
        TestAssert.IsFalse(towers.IsLocalCharging(101, 1));
        TestAssert.AreEqual(1, towers.GetChargerCount(101, 1));
        mecha.coreEnergy = 100;
        player.isAlive = false;
        towers.UpdateLocalState(power, multithreaded);
        TestAssert.IsFalse(towers.IsLocalCharging(101, 1));
        player.isAlive = true;

        foreach (var altitude in new[] { 100f, 251f })
        {
            testPosition = power.multithreadPlayerPos = new Vector3(0, altitude, 0);
            towers.UpdateLocalState(power, multithreaded);
            TestAssert.IsFalse(towers.IsLocalCharging(101, 1));
        }
        testPosition = power.multithreadPlayerPos = near;
        power.nodePool[1].powerPoint.x = 10.7f / 0.988f;
        towers.UpdateLocalState(power, multithreaded);
        TestAssert.IsTrue(towers.IsLocalCharging(101, 1));
        power.nodePool[1].powerPoint.x = 10.72f / 0.988f;
        towers.UpdateLocalState(power, multithreaded);
        TestAssert.IsFalse(towers.IsLocalCharging(101, 1));
        power.nodePool[1].coverRadius = 15f;
        power.nodePool[1].powerPoint.x = 16f / 0.988f;
        towers.UpdateLocalState(power, multithreaded);
        TestAssert.IsFalse(towers.IsLocalCharging(101, 1));
        power.nodePool[1].powerPoint.x = 0;
        towers.UpdateLocalState(power, multithreaded);
        TestAssert.IsTrue(towers.IsLocalCharging(101, 1));
        towers.ClearLocalState();
        TestAssert.IsFalse(towers.IsLocalCharging(101, 1));
        TestAssert.AreEqual(1, towers.GetChargerCount(102, 1));
    }

    [TestMethod]
    public void RemoteDemandNeverChargesLocalMechaAndSinglePlayerKeepsVanillaEnergy()
    {
        var type = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Transpilers.PowerSystem_Transpiler")!;
        var demand = type.GetMethod("SetChargerRequiredPower", BindingFlags.Static | BindingFlags.NonPublic)!;
        var charge = type.GetMethod("AddMechaEnergy", BindingFlags.Static | BindingFlags.NonPublic)!;
        towers.ApplyRemoteState(new PowerTowerChargerUpdate(2, 101, [1]));
        object[] args = [power, power.nodePool[1]];
        TestAssert.IsTrue((bool)demand.Invoke(null, args)!);
        TestAssert.AreEqual(110, ((PowerNodeComponent)args[1]).requiredEnergy);
        charge.Invoke(null, [power, 1]);
        TestAssert.AreEqual(100d, mecha.coreEnergy);
        TestAssert.AreEqual(0, mecha.chargerCount);

        testPosition = new Vector3(0, 200.2f, 0);
        towers.UpdateLocalState(power, false);
        TestAssert.AreEqual(210, UpdateDemand());
        charge.Invoke(null, [power, 1]);
        TestAssert.AreEqual(150d, mecha.coreEnergy); // (110 - 10) * 50% supplied power.
        TestAssert.AreEqual(50d, mecha.energyChanges[2]);
        TestAssert.AreEqual(42, mecha.chargerDevice[0]);
        AccessTools.Field(typeof(Player), "_planetId").SetValue(player, 102);
        charge.Invoke(null, [power, 1]);
        TestAssert.AreEqual(150d, mecha.coreEnergy);

        Multiplayer.Session = null!;
        args[1] = new PowerNodeComponent { requiredEnergy = 57 };
        TestAssert.IsFalse((bool)demand.Invoke(null, args)!);
        TestAssert.AreEqual(57, ((PowerNodeComponent)args[1]).requiredEnergy);
        power.nodePool[1].requiredEnergy = 57;
        mecha.coreEnergy = 990;
        charge.Invoke(null, [power, 1]);
        TestAssert.AreEqual(1000d, mecha.coreEnergy);
        TestAssert.AreEqual(73d, mecha.energyChanges[2]); // Vanilla uses the actual demand: (57 - 10) * 50%.
    }

    [TestMethod]
    [DataRow(0, 10, 1f, 0)]
    [DataRow(1, 110, 1f, 100)]
    [DataRow(2, 210, 1f, 100)]
    [DataRow(3, 310, 1f, 100)]
    [DataRow(1, 110, 0.5f, 50)]
    [DataRow(2, 210, 0.5f, 50)]
    [DataRow(3, 310, 0.5f, 50)]
    [DataRow(3, 310, 0f, 0)]
    public void DemandCountsEveryPlayerButEachMechaReceivesOnlyOneShare(int count, int demand, float supply, int charge)
    {
        if (count > 0)
        {
            testPosition = new Vector3(0, 200.2f, 0);
            towers.UpdateLocalState(power, false);
        }
        for (ushort id = 2; id <= count; id++)
            towers.ApplyRemoteState(new PowerTowerChargerUpdate(id, 101, [1]));
        power.networkServes[1] = supply;

        TestAssert.AreEqual(demand, UpdateDemand());
        ChargeLocalMecha();

        TestAssert.AreEqual(100d + charge, mecha.coreEnergy);
        TestAssert.AreEqual((double)charge, mecha.energyChanges[2]);
        TestAssert.AreEqual(count > 0 ? 1 : 0, mecha.chargerCount);
    }

    [TestMethod]
    [DataRow("full")]
    [DataRow("dead")]
    [DataRow("range")]
    [DataRow("planet")]
    public void LocalPlayerStoppingChargingRemovesOnlyTheirPowerDemand(string reason)
    {
        testPosition = new Vector3(0, 200.2f, 0);
        towers.UpdateLocalState(power, false);
        towers.ApplyRemoteState(new PowerTowerChargerUpdate(2, 101, [1]));
        TestAssert.AreEqual(210, UpdateDemand());

        switch (reason)
        {
            case "full": mecha.coreEnergy = mecha.coreEnergyCap; break;
            case "dead": player.isAlive = false; break;
            case "range": testPosition = new Vector3(200.2f, 0, 0); break;
            case "planet": towers.ClearLocalState(); break;
        }
        if (reason != "planet") towers.UpdateLocalState(power, false);

        TestAssert.AreEqual(110, UpdateDemand());
        var energy = mecha.coreEnergy;
        ChargeLocalMecha();
        TestAssert.AreEqual(energy, mecha.coreEnergy);
    }

    [TestMethod]
    public void DisconnectAndDemolitionRemoveDemandWithoutAffectingAnotherPlanet()
    {
        towers.ApplyRemoteState(new PowerTowerChargerUpdate(2, 101, [1]));
        towers.ApplyRemoteState(new PowerTowerChargerUpdate(3, 101, [1]));
        towers.ApplyRemoteState(new PowerTowerChargerUpdate(4, 102, [1]));
        TestAssert.AreEqual(210, UpdateDemand());
        ChargeLocalMecha();
        TestAssert.AreEqual(100d, mecha.coreEnergy);

        towers.RemovePlayer(2);
        TestAssert.AreEqual(110, UpdateDemand());
        towers.RemoveNode(101, 1);
        TestAssert.AreEqual(10, UpdateDemand());
        TestAssert.AreEqual(1, towers.GetChargerCount(102, 1));
        ChargeLocalMecha();
        TestAssert.AreEqual(100d, mecha.coreEnergy);
    }

    private int UpdateDemand()
    {
        var type = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Transpilers.PowerSystem_Transpiler")!;
        object[] args = [power, power.nodePool[1]];
        TestAssert.IsTrue((bool)type.GetMethod("SetChargerRequiredPower", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!);
        power.nodePool[1] = (PowerNodeComponent)args[1];
        return power.nodePool[1].requiredEnergy;
    }

    private void ChargeLocalMecha()
    {
        var type = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Transpilers.PowerSystem_Transpiler")!;
        type.GetMethod("AddMechaEnergy", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [power, 1]);
    }

    [TestMethod]
    public void AuthorityClientActuallyReceivesWirelessChargingEnergy()
    {
        var oldProbe = global::NebulaeModel.Authority.AuthorityRuleGuard.Probe;
        try
        {
            global::NebulaeModel.Authority.AuthorityRuleGuard.Probe = () => new(
                true, false, false);
            testPosition = new Vector3(0, 200.2f, 0);
            towers.UpdateLocalState(power, false);
            var type = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Transpilers.PowerSystem_Transpiler")!;
            type.GetMethod("AddMechaEnergy", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [power, 1]);
            TestAssert.AreEqual(150d, mecha.coreEnergy);
            TestAssert.AreEqual(50d, mecha.energyChanges[2]);
            TestAssert.AreEqual(42, mecha.chargerDevice[0]);
        }
        finally { global::NebulaeModel.Authority.AuthorityRuleGuard.Probe = oldProbe; }
    }

    private static bool GetTestPosition(ref Vector3 __result)
    {
        __result = testPosition;
        return false;
    }

}
