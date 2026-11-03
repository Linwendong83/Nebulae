using System.Reflection;
using NebulaeModel.Packets.Combat.Mecha;
using System.Reflection.Emit;
using HarmonyLib;
using global::NebulaeModel.DataStructures;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Authority;
using NebulaeWorld;
using NebulaeWorld.Combat;
using NebulaeWorld.GameStates;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class PlayerReconnectLifeTest
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void LivingReconnectClearsAnInterruptedRespawnWithoutResettingEnergy(int mode)
    {
        var previous = GameMain.data;
        var transaction = PlayerLifeData.CurrentTransactionId;
        var dropped = PlayerLifeData.CurrentRedeployItemsDropped;
        try
        {
            var game = Uninitialized<GameData>();
            var player = Uninitialized<Player>();
            var controller = Uninitialized<PlayerController>();
            var mecha = Uninitialized<Mecha>();
            SetBackingField(player, "mecha", mecha); SetBackingField(player, "controller", controller); SetBackingField(game, "mainPlayer", player);
            GameMain.data = game;
            controller.actionDeath = new PlayerAction_Death
            { respawnMode = mode, respawnStage = 1, respawnTick = 80, selectedRespawnOption = 4 };
            mecha.hp = 302400; mecha.energyShieldEnergy = 35418199; mecha.coreEnergy = 123456;
            var data = new PlayerData(2, 103)
            { Life = new PlayerLifeData { IsAlive = true, DeathCount = 2, Revision = 100 } };
            var life = new PlayerLifeManager();
            for (var i = 0; i < 3; i++)
            {
                life.RestoreLocal(data);
                TestAssert.IsTrue(player.isAlive);
                TestAssert.AreEqual(2, player.deathCount);
                TestAssert.IsFalse(controller.actionDeath.respawning);
                TestAssert.AreEqual(-1, controller.actionDeath.selectedRespawnOption);
                TestAssert.AreEqual(302400, mecha.hp);
                TestAssert.AreEqual(35418199L, mecha.energyShieldEnergy);
                TestAssert.AreEqual(123456d, mecha.coreEnergy);
            }
        }
        finally
        {
            GameMain.data = previous;
            PlayerLifeData.CurrentTransactionId = transaction;
            PlayerLifeData.CurrentRedeployItemsDropped = dropped;
        }
    }

    [TestMethod]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    public void RespawnReceiptCannotUndoACompletedLifeCycle(int receiptDeath, bool shouldRemainAlive)
    {
        var player = new PlayerData(2, 103, "test")
        { PersistentId = "owner", Life = new PlayerLifeData { IsAlive = true, DeathCount = 1, Revision = 100 } };
        var receipt = new PlayerData(2, 103, "test")
        { Life = new PlayerLifeData { IsAlive = false, DeathCount = receiptDeath, RespawnMode = 2, Revision = 200 } };
        var writer = new NetDataWriter(); receipt.Serialize(writer);
        var manager = new MetadataTransactionManager();
        AccessTools.Field(typeof(MetadataTransactionManager), "initialized").SetValue(manager, true);
        var journal = (Dictionary<string, MetadataTransaction>)AccessTools.Field(typeof(MetadataTransactionManager), "serverTransactions").GetValue(manager);
        journal.Add("receipt", new MetadataTransaction
        {
            Id = "receipt",
            Owner = "owner",
            Operation = MetadataOperation.Respawn,
            State = MetadataTransactionState.Applied,
            Stamp = receiptDeath,
            AfterPlayer = writer.CopyData()
        });
        manager.RestoreServerPlayer(player);
        TestAssert.AreEqual(shouldRemainAlive, player.Life.IsAlive);
        TestAssert.AreEqual(shouldRemainAlive ? 1 : receiptDeath, player.Life.DeathCount);
        TestAssert.AreEqual(shouldRemainAlive ? 100L : 200L, player.Life.Revision);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(3)]
    public void DeadReconnectKeepsPendingRespawnAndDoesNotRepeatTheInventoryDrop(int mode)
    {
        var previous = GameMain.data;
        var transaction = PlayerLifeData.CurrentTransactionId;
        var dropped = PlayerLifeData.CurrentRedeployItemsDropped;
        var harmony = new Harmony("nebulae.tests.dead-reconnect");
        // Wreckage rendering needs a Unity scene; the restoration logic remains real.
        harmony.Patch(AccessTools.Method(typeof(PlayerLifeManager), "RestoreLocal"),
            transpiler: new HarmonyMethod(typeof(PlayerReconnectLifeTest), nameof(WithoutWreckageRendering)));
        try
        {
            var game = Uninitialized<GameData>();
            var player = Uninitialized<Player>();
            var controller = Uninitialized<PlayerController>();
            var mecha = Uninitialized<Mecha>();
            SetBackingField(player, "mecha", mecha); SetBackingField(player, "controller", controller); SetBackingField(game, "mainPlayer", player);
            SetBackingField(player, "mechaArmorModel", Uninitialized<MechaArmorModel>());
            GameMain.data = game;
            controller.actionDeath = new PlayerAction_Death();
            var data = new PlayerData(2, 103)
            {
                Life = new PlayerLifeData
                {
                    IsAlive = false,
                    DeathCount = 1,
                    RespawnMode = mode,
                    RespawnStage = 1,
                    RespawnTick = 80,
                    TransactionId = "paid",
                    RedeployItemsDropped = true,
                    Revision = 100
                }
            };
            new PlayerLifeManager().RestoreLocal(data);
            TestAssert.IsFalse(player.isAlive);
            TestAssert.AreEqual(1, player.deathCount);
            TestAssert.AreEqual(mode, controller.actionDeath.respawnMode);
            TestAssert.AreEqual(0, controller.actionDeath.respawnStage);
            TestAssert.AreEqual(0, controller.actionDeath.respawnTick);
            TestAssert.AreEqual("paid", PlayerLifeData.CurrentTransactionId);
            TestAssert.IsTrue(PlayerLifeData.CurrentRedeployItemsDropped);
        }
        finally
        {
            harmony.UnpatchSelf();
            GameMain.data = previous;
            PlayerLifeData.CurrentTransactionId = transaction;
            PlayerLifeData.CurrentRedeployItemsDropped = dropped;
        }
    }

    [TestMethod]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(3, false)]
    public void CombatFactsPreserveCompletedRespawnButAcceptANewDeath(int deaths, bool stale)
    {
        var previousGame = GameMain.data;
        var previousSession = Multiplayer.Session;
        var loaded = AccessTools.Field(typeof(StorageComponent), "staticLoaded");
        var wasLoaded = loaded.GetValue(null);
        loaded.SetValue(null, true);
        var harmony = new Harmony("nebulae.tests.reconnect-life");
        // Setup loads Unity fleet assets; the actual combat serialization and application run unmodified.
        harmony.Patch(AccessTools.Method(typeof(CombatModuleComponent), "Setup", [typeof(int), typeof(GameData)]),
            prefix: new HarmonyMethod(typeof(PlayerReconnectLifeTest), nameof(SkipFleetAssets)));
        harmony.Patch(AccessTools.Method(typeof(Player), "Kill"),
            prefix: new HarmonyMethod(typeof(PlayerReconnectLifeTest), nameof(RecordDeath)));
        try
        {
            var game = Uninitialized<GameData>();
            var player = Uninitialized<Player>();
            var mecha = Uninitialized<Mecha>();
            SetBackingField(player, "mecha", mecha); SetBackingField(game, "mainPlayer", player);
            mecha.ammoStorage = new StorageComponent(3);
            mecha.bombStorage = new StorageComponent(1);
            mecha.fighterStorage = new StorageComponent(5);
            mecha.groundCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] };
            mecha.spaceCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] };
            GameMain.data = game;
            var writer = new NetDataWriter(); new MechaFightData(player).Serialize(writer);
            player.isAlive = true; player.deathCount = 2; player.invincibleTicks = 250;
            mecha.hp = 302400; mecha.energyShieldEnergy = 35418199; mecha.laserEnergy = 600;
            mecha.hpRecoverCD = 15; mecha.energyShieldRecoverCD = 25;
            var session = Uninitialized<MultiplayerSession>();
            session.World = new SimulatedWorld();
            session.LocalPlayer = new LocalPlayer { Data = new PlayerData(2, 103) };
            Multiplayer.Session = session;
            var authority = new CombatAuthorityManager();
            var packet = new AuthorityPlayerCombatStatePacket
            {
                PlayerId = 2,
                ActorConnection = 1,
                CombatRevision = 1,
                IsAlive = false,
                DeathCount = deaths,
                CombatData = writer.CopyData()
            };
            TestAssert.IsTrue(authority.ApplyState(packet));
            TestAssert.AreEqual(stale, player.isAlive);
            TestAssert.AreEqual(stale ? 2 : 3, player.deathCount);
            TestAssert.AreEqual(stale ? 250 : 0, player.invincibleTicks);
            TestAssert.AreEqual(stale ? 302400 : 0, mecha.hp);
            TestAssert.AreEqual(stale ? 35418199L : 0L, mecha.energyShieldEnergy);
            TestAssert.AreEqual(stale ? 600L : 0L, mecha.laserEnergy);
            TestAssert.AreEqual(stale ? 15 : 0, mecha.hpRecoverCD);
            TestAssert.AreEqual(stale ? 25 : 0, mecha.energyShieldRecoverCD);
            TestAssert.AreEqual(1L, authority.LastAppliedRevision,
                "The completion checkpoint must still acknowledge a fresh host stock baseline.");
            // A subsequent live state must still update combat values and cumulative costs.
            packet.IsAlive = true; packet.DeathCount = player.deathCount; packet.CombatRevision = 2;
            writer = new NetDataWriter();
            new MechaFightData(player) { Hp = 280000, EnergyShieldEnergy = 12000, LaserEnergy = 500 }.Serialize(writer);
            packet.CombatData = writer.CopyData();
            mecha.coreEnergy = 100;
            packet.CoreEnergyDebitTotal = 7;
            TestAssert.IsTrue(authority.ApplyState(packet));
            TestAssert.AreEqual(93d, mecha.coreEnergy);
            TestAssert.AreEqual(280000, mecha.hp);
            TestAssert.AreEqual(12000L, mecha.energyShieldEnergy);
            TestAssert.IsTrue(authority.ApplyState(packet));
            TestAssert.AreEqual(93d, mecha.coreEnergy, "A duplicate must not charge twice.");
            packet.ActorConnection = 0; packet.CombatRevision = 99; packet.CoreEnergyDebitTotal = 100;
            TestAssert.IsTrue(authority.ApplyState(packet));
            TestAssert.AreEqual(93d, mecha.coreEnergy, "An old connection must not overwrite the new seat.");
        }
        finally
        {
            harmony.UnpatchSelf();
            loaded.SetValue(null, wasLoaded);
            GameMain.data = previousGame;
            Multiplayer.Session = previousSession;
        }
    }

    private static bool SkipFleetAssets() => false;

    private static IEnumerable<CodeInstruction> WithoutWreckageRendering(IEnumerable<CodeInstruction> instructions)
    {
        var render = AccessTools.Method(typeof(MechaArmorModel), "Kill");
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(render))
            {
                instruction.opcode = OpCodes.Pop;
                instruction.operand = null;
            }
            yield return instruction;
        }
    }

    private static bool RecordDeath(Player __instance)
    {
        __instance.isAlive = false;
        __instance.deathCount++;
        return false;
    }

}

[TestClass]
public class PlayerLifeReceiptTest
{
    [TestMethod]
    public void RejectedLifeSnapshotsDoNotConfirmPersonalState()
    {
        var receipt = PlayerLifePacket.CreateReceipt(2, 12345, CheckpointReceiptStatus.Retry, 12000, CheckpointRejectReason.Busy);
        TestAssert.IsTrue(receipt.Acknowledgement);
        TestAssert.AreEqual(12345L, receipt.Life.Revision);
        var life = new PlayerLifeManager();
        var ticket = life.RequestCheckpoint();
        SetField(life, "pendingTicket", ticket);
        var pending = new PlayerLifePacket { Life = new PlayerLifeData { Revision = 12345 } };
        var field = typeof(PlayerLifeManager).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic);
        field!.SetValue(life, pending);
        life.Acknowledge(receipt);
        TestAssert.IsNull(field.GetValue(life));
        TestAssert.AreEqual(0L, life.AcceptedTicket);
        TestAssert.IsTrue(life.BlocksInventory, "A refused snapshot still needs a confirmed replacement.");
    }
}
