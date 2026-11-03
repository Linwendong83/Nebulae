using HarmonyLib;
using global::NebulaeModel.DataStructures;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Combat.Mecha;
using NebulaeWorld;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class MechaSettingsPersistenceTest
{
    private GameData previousGame = null!;
    private MultiplayerSession previousSession = null!;
    private bool previousLoaded;
    private int[] previousStacks = null!;
    private Harmony harmony = null!;

    [TestInitialize]
    public void SetUp()
    {
        previousGame = GameMain.data;
        previousSession = Multiplayer.Session;
        previousLoaded = StorageComponent.staticLoaded;
        previousStacks = StorageComponent.itemStackCount;
        StorageComponent.staticLoaded = true;
        StorageComponent.itemStackCount = Enumerable.Repeat(100, 12000).ToArray();
        GameMain.data = Uninitialized<GameData>();
        Multiplayer.Session = null;
        // Fleet prefab transforms require a Unity scene; the checkpoint itself contains no fleets.
        harmony = new Harmony("nebulae.tests.mecha-settings");
        harmony.Patch(AccessTools.Method(typeof(CombatModuleComponent), "Setup", [typeof(int), typeof(GameData)]),
            prefix: new HarmonyMethod(typeof(MechaSettingsPersistenceTest), nameof(SkipAssets)));
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchAll(harmony.Id);
        GameMain.data = previousGame;
        Multiplayer.Session = previousSession;
        StorageComponent.staticLoaded = previousLoaded;
        StorageComponent.itemStackCount = previousStacks;
    }

    [TestMethod]
    [DataRow(false, true, 1f)]
    [DataRow(true, false, 3f)]
    public void AcceptedCheckpointAndServerSaveRestorePersonalSettings(bool warper, bool shield, float speed)
    {
        var source = Player();
        source.mecha.autoReplenishWarper = warper;
        source.mecha.energyShieldRechargeEnabled = shield;
        source.mecha.energyShieldRechargeSpeed = speed;
        var snapshot = new PlayerData(2, 101)
        {
            Mecha = new MechaData(source),
            Life = new PlayerLifeData { Revision = 10, IsAlive = true }
        };
        var stored = new PlayerData(2, 101);
        TestAssert.AreEqual(CheckpointReceiptStatus.Accepted,
            PlayerLifeManager.StoreServerResult(stored, SerializePlayer(snapshot), out _));

        // The .server save uses the same player serializer as the join snapshot.
        var saved = SerializePlayer(stored);
        var restored = new PlayerData(); restored.Deserialize(new NetDataReader(saved));
        var destination = Player();
        destination.mecha.autoReplenishWarper = !warper;
        destination.mecha.energyShieldRechargeEnabled = !shield;
        destination.mecha.energyShieldRechargeSpeed = 2f;
        restored.Mecha.UpdateMech(destination);
        SimulatedWorld.FixPlayerAfterImport();
        AssertSettings(destination.mecha, warper, shield, speed);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public void RejoinKeepsIndependentDroneSwitchesAndClearsOldTargets(int flags)
    {
        var source = Player();
        var module = source.mecha.constructionModule;
        module.droneEnabled = (flags & 1) != 0;
        module.droneConstructEnabled = (flags & 2) != 0;
        module.droneRepairEnabled = (flags & 4) != 0;
        module.autoReconstruct = false;
        SetBackingField(module, nameof(ConstructionModuleComponent.dronePriority), 2);
        module.buildTargets = [new ConstructTarget { objectId = 777 }];
        module.repairTargets = [new ConstructTargetExtra { objectId = 888 }];
        module.autoReconstructTargets = [new ConstructTarget { objectId = 999 }];
        var saved = Serialize(new MechaData(source));
        var restored = new MechaData(); restored.Deserialize(new NetDataReader(saved));
        var destination = Player();
        restored.UpdateMech(destination);
        SimulatedWorld.FixPlayerAfterImport();

        var actual = destination.mecha.constructionModule;
        TestAssert.AreEqual((flags & 1) != 0, actual.droneEnabled);
        TestAssert.AreEqual((flags & 2) != 0, actual.droneConstructEnabled);
        TestAssert.AreEqual((flags & 4) != 0, actual.droneRepairEnabled);
        TestAssert.IsFalse(actual.autoReconstruct);
        TestAssert.AreEqual(2, actual.dronePriority);
        TestAssert.AreEqual(0, actual.buildTargets[0].objectId);
        TestAssert.AreEqual(0, actual.repairTargets[0].objectId);
        TestAssert.AreEqual(0, actual.autoReconstructTargets[0].objectId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OldCheckpointsUseNativeDefaultsWithoutLeakingAnotherPlayersSettings(bool diskImport)
    {
        var current = Serialize(new MechaData(Player())
        { AutoReplenishWarper = true, ShieldRechargeEnabled = false, ShieldRechargeSpeed = 3f });
        var legacy = RewritePayload(current, payload => payload.Take(payload.Length - 10).ToArray());
        var reused = new MechaData();
        Read(reused, current, diskImport);
        Read(reused, legacy, diskImport);
        var destination = Player();
        destination.mecha.autoReplenishWarper = true;
        destination.mecha.energyShieldRechargeEnabled = false;
        destination.mecha.energyShieldRechargeSpeed = 3f;
        reused.UpdateMech(destination);
        AssertSettings(destination.mecha, false, true, 2f);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SettingsTailRejectsTruncationUnknownMarkersAndInvalidSpeeds(bool diskImport)
    {
        var current = Serialize(new MechaData(Player()));
        for (var removed = 1; removed < 10; removed++)
        {
            var count = removed;
            Reject(payload => payload.Take(payload.Length - count).ToArray());
        }
        Reject(payload => payload.Concat(new byte[] { 0 }).ToArray());
        Reject(payload => { payload[payload.Length - 10] ^= 1; return payload; });
        foreach (var speed in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f, 4f })
            Reject(payload => { BitConverter.GetBytes(speed).CopyTo(payload, payload.Length - 4); return payload; });

        void Reject(Func<byte[], byte[]> mutate) => TestAssert.Throws<InvalidDataException>(() =>
            Read(new MechaData(), RewritePayload(current, mutate), diskImport));
    }

    private static Player Player()
    {
        var player = Uninitialized<Player>();
        player.packageColCount = 10;
        var delivery = new DeliveryPackage(); delivery.Init();
        SetBackingField(player, nameof(global::Player.deliveryPackage), delivery);
        SetBackingField(player, nameof(global::Player.package), new StorageComponent(10));
        SetBackingField(player, nameof(global::Player.mecha), new Mecha
        {
            player = player,
            reactorStorage = new StorageComponent(4),
            warpStorage = new StorageComponent(1),
            ammoStorage = new StorageComponent(3),
            bombStorage = new StorageComponent(1),
            fighterStorage = new StorageComponent(5),
            forge = new MechaForge { tasks = [], extraItems = new ItemBundle() },
            constructionModule = new ConstructionModuleComponent(),
            groundCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] },
            spaceCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] },
            energyShieldRechargeEnabled = true,
            energyShieldRechargeSpeed = 2f
        });
        SetBackingField(GameMain.data, nameof(GameData.mainPlayer), player);
        return player;
    }

    private static byte[] Serialize(MechaData mecha)
    {
        var writer = new NetDataWriter(); mecha.Serialize(writer); return writer.CopyData();
    }

    private static void Read(MechaData mecha, byte[] bytes, bool diskImport)
    {
        var reader = new NetDataReader(bytes);
        if (diskImport) mecha.Import(reader, 13);
        else mecha.Deserialize(reader);
        TestAssert.AreEqual(0, reader.AvailableBytes);
    }

    private static byte[] RewritePayload(byte[] bytes, Func<byte[], byte[]> mutate)
    {
        var reader = new NetDataReader(bytes);
        new PlayerTechBonuses().Deserialize(reader);
        reader.GetLong(); reader.GetDouble(); reader.GetDouble(); reader.GetBool();
        new MechaFightData().Deserialize(reader);
        var offset = reader.Position;
        var payload = mutate(reader.GetBytesWithLength());
        return bytes.Take(offset).Concat(BitConverter.GetBytes(payload.Length)).Concat(payload).ToArray();
    }

    private static void AssertSettings(Mecha mecha, bool warper, bool shield, float speed)
    {
        TestAssert.AreEqual(warper, mecha.autoReplenishWarper);
        TestAssert.AreEqual(shield, mecha.energyShieldRechargeEnabled);
        TestAssert.AreEqual(speed, mecha.energyShieldRechargeSpeed);
    }

    private static bool SkipAssets() => false;
}
