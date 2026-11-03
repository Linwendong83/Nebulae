using System.Reflection.Emit;
using HarmonyLib;
using global::NebulaeModel.DataStructures;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.GameStates;
using NebulaeModel.Utils;
using NebulaeNetwork;
using NebulaeWorld;
using NebulaeWorld.GameDataHistory;
using NebulaeWorld.GameStates;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class RespawnSettlementTest
{
    public TestContext TestContext { get; set; } = null!;
    private readonly Harmony harmony = new("nebulae.tests.respawn-settlement");
    private MultiplayerSession? previousSession;
    private GameData? previousGame;
    private PropertySystem previousProperties = null!;
    private AccountData previousAccount;
    private bool previousDedicated;
    private object? previousPropertyPath;
    private string previousTransaction = "";
    private bool previousDropped;
    private string root = "";
    private MetadataTransactionManager client = null!, server = null!;
    private PlayerData local = null!, remote = null!;
    private static readonly Queue<MetadataOperationPacket> Outbound = new();
    private static int writes, failWrite, starts;

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session; previousGame = GameMain.data;
        previousProperties = DSPGame.propertySystem; previousAccount = AccountData.me;
        previousDedicated = Multiplayer.IsDedicated;
        previousPropertyPath = AccessTools.Field(typeof(GameConfig), "propertyPath").GetValue(null);
        previousTransaction = PlayerLifeData.CurrentTransactionId;
        previousDropped = PlayerLifeData.CurrentRedeployItemsDropped;
        root = Path.Combine(Path.GetTempPath(), "nebulae-respawn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SetStatic(typeof(GameConfig), "propertyPath", root + Path.DirectorySeparatorChar);
        AccountData.me = new AccountData { platform = ESalePlatform.Standalone, userId = 42 };
        Multiplayer.IsDedicated = false;
        var game = Uninitialized<GameData>();
        game.gameDesc = new GameDesc { starCount = 64, galaxySeed = 123, resourceMultiplier = 1 };
        game.history = Uninitialized<GameHistoryData>();
        game.history.featureValues = new Dictionary<int, int>();
        game.history.propertyData = new ClusterPropertyData();
        game.history.propertyData.Init(game.GetClusterSeedKey());
        var player = Uninitialized<Player>();
        var controller = Uninitialized<PlayerController>();
        controller.actionDeath = new PlayerAction_Death();
        SetBackingField(player, "controller", controller);
        SetBackingField(game, "mainPlayer", player);
        player.isAlive = false; player.deathCount = 1;
        GameMain.data = game;
        local = new PlayerData(2, 101) { PersistentId = "owner" };
        remote = new PlayerData(2, 101)
        { PersistentId = "owner", Life = new PlayerLifeData { IsAlive = false, DeathCount = 1 } };
        var session = Uninitialized<MultiplayerSession>();
        session.LocalPlayer = new LocalPlayer { Data = local };
        session.IsGameLoaded = true;
        session.Network = Uninitialized<Client>();
        session.History = new GameDataHistoryManager();
        Multiplayer.Session = session;
        DSPGame.propertySystem = new PropertySystem { propertyDatas = new List<ClusterPropertyData>() };
        foreach (var item in PropertySystem.matrixIds)
            DSPGame.propertySystem.SetItemProduction(game.GetClusterSeedKey(), item, 10000);
        harmony.Patch(AccessTools.Method(typeof(PropertyAccountStore), "Save"),
            transpiler: new HarmonyMethod(typeof(RespawnSettlementTest), nameof(PrivateExportBoundary)));
        PropertyAccountStore.Save();
        client = Manager(false); server = Manager(true);
        session.PropertyTransactions = client;
        Outbound.Clear(); writes = failWrite = starts = 0;
        Patch(typeof(Client), "SendPacket", nameof(Send), true, typeof(MetadataOperationPacket));
        Patch(typeof(Server), "SendPacket", nameof(Skip), true, typeof(PropertyHistoryPacket));
        Patch(typeof(MetadataManager), "get_LocalIdentity", nameof(Identity));
        Patch(typeof(MetadataTransactionManager), "CapturePlayer", nameof(Capture));
        Patch(typeof(MetadataTransactionManager), "RestorePlayer", nameof(Skip));
        Patch(typeof(PlayerAction_Death), "Respawn", nameof(Respawn));
        Patch(typeof(UIRealtimeTip), "Popup", nameof(Skip));
        harmony.Patch(AccessTools.Method(typeof(AtomicFile), "Write", [typeof(string), typeof(byte[]), typeof(int)]),
            prefix: new HarmonyMethod(typeof(RespawnSettlementTest), nameof(BeforeWrite)));
    }

    private void Patch(Type type, string name, string prefix, bool generic = false, Type? argument = null)
    {
        var method = type == typeof(UIRealtimeTip)
            ? AccessTools.Method(type, name, [typeof(string), typeof(bool), typeof(int)])
            : AccessTools.Method(type, name);
        if (generic) method = method.MakeGenericMethod(argument!);
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(RespawnSettlementTest), prefix));
    }

    [TestCleanup]
    public void CleanUp()
    {
        harmony.UnpatchSelf();
        Multiplayer.Session = previousSession; GameMain.data = previousGame;
        DSPGame.propertySystem = previousProperties; AccountData.me = previousAccount;
        Multiplayer.IsDedicated = previousDedicated;
        SetStatic(typeof(GameConfig), "propertyPath", previousPropertyPath);
        PlayerLifeData.CurrentTransactionId = previousTransaction;
        PlayerLifeData.CurrentRedeployItemsDropped = previousDropped;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private MetadataTransactionManager Manager(bool host)
    {
        var result = new MetadataTransactionManager();
        SetField(result, "initialized", true);
        SetField(result, host ? "serverPath" : "localPath", Path.Combine(root, host ? "server.bin" : "local.bin"));
        return result;
    }

    private static Dictionary<string, MetadataTransaction> Transactions(MetadataTransactionManager manager, bool host) =>
        (Dictionary<string, MetadataTransaction>)AccessTools.Field(typeof(MetadataTransactionManager), host ? "serverTransactions" : "localTransactions").GetValue(manager);

    private MetadataOperationPacket Deliver(MetadataOperationPacket packet)
    {
        MetadataOperationPacket? response = null;
        var session = Multiplayer.Session;
        session.Server = Uninitialized<Server>();
        session.LocalPlayer.Data = new PlayerData(1, 101);
        try { server.HandleServer(packet, remote, value => response = value); }
        finally { session.Server = null; session.LocalPlayer.Data = local; }
        return response!;
    }

    private void Finish()
    {
        var iterations = 0;
        while (Outbound.Count > 0)
        {
            TestAssert.IsLessThan(10, iterations++);
            var response = Deliver(Outbound.Dequeue());
            if (response != null) client.Receive(response);
        }
    }

    private static bool Send(MetadataOperationPacket packet) { Outbound.Enqueue(RoundTrip(packet)); return false; }
    private static IEnumerable<CodeInstruction> PrivateExportBoundary(IEnumerable<CodeInstruction> instructions)
    {
        var export = AccessTools.Method(typeof(PropertySystem), "Export");
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(export))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(RespawnSettlementTest), nameof(ExportProperties));
            }
            yield return instruction;
        }
    }
    private static void ExportProperties(PropertySystem properties, BinaryWriter writer) =>
        AccessTools.Method(typeof(PropertySystem), "Export").Invoke(properties, [writer]);
    private static bool Skip() => false;
    private static bool Identity(ref string __result) { __result = "owner"; return false; }
    private static bool BeforeWrite()
    {
        if (++writes == failWrite) throw new IOException("Injected durable write failure");
        return true;
    }
    private static bool Respawn(PlayerAction_Death __instance, int _respawnMode)
    { starts++; __instance.respawnMode = _respawnMode; return false; }
    private static bool Capture(ref byte[] __result)
    {
        var snapshot = new PlayerData(2, 101) { Life = PlayerLifeData.Capture(GameMain.mainPlayer) };
        var writer = new NetDataWriter(); snapshot.Serialize(writer); __result = writer.CopyData();
        return false;
    }

    [TestMethod]
    [DataRow(2, false)]
    [DataRow(3, false)]
    [DataRow(2, true)]
    [DataRow(3, true)]
    public void FixedPriceRespawnUsesOneRoundTripAndFourDurableWrites(int mode, bool sandbox)
    {
        GameMain.data.gameDesc.isSandboxMode = sandbox;
        client.Request(MetadataOperation.Respawn, mode);
        TestAssert.HasCount(1, Outbound);
        var request = Outbound.Dequeue();
        TestAssert.AreEqual(MetadataMessage.RespawnCommit, request.Message);
        TestAssert.AreEqual(2, writes);
        TestAssert.AreEqual(0, starts);
        var result = Deliver(request);
        TestAssert.AreEqual(MetadataMessage.Result, result.Message);
        TestAssert.AreEqual(3, writes);
        client.Receive(result);
        TestAssert.AreEqual(1, starts);
        TestAssert.AreEqual(sandbox ? 0 : mode == 2 ? 12 : 6,
            DSPGame.propertySystem.GetItemConsumption(GameMain.data.GetClusterSeedKey(), 6001));
        Finish();
        TestAssert.IsFalse(client.Busy);
    }

    [TestMethod]
    public void DoubleClickAndRepeatedResultsDoNotChargeOrRespawnAgain()
    {
        client.Request(MetadataOperation.Respawn, 2);
        client.Request(MetadataOperation.Respawn, 2);
        TestAssert.HasCount(1, Outbound);
        var request = Outbound.Dequeue();
        var result = Deliver(request);
        client.Receive(result);
        client.Receive(result);
        Finish();
        remote.Life.IsAlive = true;
        var before = writes;
        client.Receive(Deliver(request));
        TestAssert.AreEqual(before, writes);
        TestAssert.AreEqual(1, starts);
        TestAssert.AreEqual(12, DSPGame.propertySystem.GetItemConsumption(GameMain.data.GetClusterSeedKey(), 6001));
        TestAssert.AreEqual(12, GameMain.history.GetPropertyItemComsumption(6001));
    }

    [TestMethod]
    [DataRow("price")]
    [DataRow("death")]
    [DataRow("alive")]
    public void InvalidAuthoritativeStateRefundsTheWholePayment(string conflict)
    {
        client.Request(MetadataOperation.Respawn, 2);
        var request = Outbound.Dequeue();
        if (conflict == "death") remote.Life.DeathCount++;
        if (conflict == "alive") remote.Life.IsAlive = true;
        if (conflict == "price")
        {
            var tx = MetadataTransaction.ImportWire(request.Transaction);
            tx.Cost[0]++;
            request.Transaction = tx.ExportWire();
        }
        var result = Deliver(request);
        TestAssert.AreEqual(MetadataTransactionState.Rejected, MetadataTransaction.ImportWire(result.Transaction).State);
        client.Receive(result);
        client.Receive(result);
        TestAssert.AreEqual(0, starts);
        TestAssert.IsFalse(client.Busy);
        TestAssert.AreEqual(0, DSPGame.propertySystem.GetItemConsumption(GameMain.data.GetClusterSeedKey(), 6001));
        TestAssert.AreEqual(0, GameMain.history.GetPropertyItemComsumption(6001));
    }

    [TestMethod]
    public void InsufficientBalanceDoesNotWriteOrSendAnything()
    {
        DSPGame.propertySystem.SetItemProduction(GameMain.data.GetClusterSeedKey(), 6001, 1);
        client.Request(MetadataOperation.Respawn, 2);
        TestAssert.AreEqual(0, writes);
        TestAssert.IsEmpty(Outbound);
        TestAssert.IsFalse(client.Busy);
    }

    [TestMethod]
    public void NormalFirstTickDoesNotImmediatelyResendTheCommit()
    {
        client.Request(MetadataOperation.Respawn, 2);
        client.GameTick();
        TestAssert.HasCount(1, Outbound);
        Finish();
    }

    [TestMethod]
    public void LostCommitReplySurvivesAClientReconnectWithoutAnotherCharge()
    {
        client.Request(MetadataOperation.Respawn, 2);
        Deliver(Outbound.Dequeue()); // The connection drops before the result reaches the client.
        ReloadWallet();
        client = Restore(false);
        client.GameTick();
        var beforeRetry = writes;
        var response = Deliver(Outbound.Dequeue());
        TestAssert.AreEqual(beforeRetry, writes, "A committed receipt needs no second host write.");
        client.Receive(response);
        Finish();
        TestAssert.AreEqual(1, starts);
        TestAssert.AreEqual(12, DSPGame.propertySystem.GetItemConsumption(GameMain.data.GetClusterSeedKey(), 6001));
        TestAssert.AreEqual(12, GameMain.history.GetPropertyItemComsumption(6001));
        TestAssert.IsFalse(client.Busy);
    }

    [TestMethod]
    public void CompletedReceiptRejectsAnotherPaymentForTheSameDeath()
    {
        client.Request(MetadataOperation.Respawn, 2);
        Finish();
        GameMain.mainPlayer.controller.actionDeath.respawnMode = 0;
        remote.Life.RespawnMode = 0; // Even a stale life checkpoint cannot permit a second receipt.
        client.Request(MetadataOperation.Respawn, 2);
        Finish();
        TestAssert.AreEqual(1, starts);
        TestAssert.AreEqual(12, DSPGame.propertySystem.GetItemConsumption(GameMain.data.GetClusterSeedKey(), 6001));
        TestAssert.AreEqual(12, GameMain.history.GetPropertyItemComsumption(6001));
    }

    [TestMethod]
    public void RetransmissionCannotChangeTheReceiptOwnerOrTerms()
    {
        client.Request(MetadataOperation.Respawn, 2);
        var packet = Outbound.Dequeue();
        var response = Deliver(packet);
        var recordedWrites = writes;
        remote.PersistentId = "other";
        TestAssert.IsNull(Deliver(packet));
        remote.PersistentId = "owner";
        var changed = MetadataTransaction.ImportWire(packet.Transaction);
        changed.Target = 3;
        TestAssert.IsNull(Deliver(new MetadataOperationPacket
        { WorldId = SaveManager.WorldId, Message = MetadataMessage.RespawnCommit, Transaction = changed.ExportWire() }));
        TestAssert.AreEqual(recordedWrites, writes);
        client.Receive(response);
        Finish();
        TestAssert.AreEqual(1, starts);
    }

    [TestMethod]
    public void MalformedWireDataCannotSuspendTheServersDurableJournal()
    {
        client.Request(MetadataOperation.Respawn, 2);
        var packet = Outbound.Dequeue();
        TestAssert.Throws<EndOfStreamException>(() => Deliver(new MetadataOperationPacket
        { WorldId = packet.WorldId, Message = packet.Message, Transaction = packet.Transaction.Take(4).ToArray() }));
        TestAssert.IsFalse(server.Busy);
        client.Receive(Deliver(packet));
        Finish();
        TestAssert.AreEqual(1, starts);
    }

    [TestMethod]
    [DataRow(4)]
    [DataRow(5)]
    public void InterruptedRefundIsReplayedBeforeThePaymentCanFinish(int boundary)
    {
        client.Request(MetadataOperation.Respawn, 2);
        remote.Life.IsAlive = true;
        var rejection = Deliver(Outbound.Dequeue());
        failWrite = boundary;
        TestAssert.Throws<IOException>(() => client.Receive(rejection));
        failWrite = 0;
        ReloadWallet();
        client = Restore(false); server = Restore(true);
        client.GameTick();
        Finish();
        TestAssert.IsFalse(client.Busy);
        TestAssert.AreEqual(0, starts);
        TestAssert.AreEqual(0, DSPGame.propertySystem.GetItemConsumption(GameMain.data.GetClusterSeedKey(), 6001));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public void EveryInterruptedWriteRecoversWithoutLosingOrMultiplyingTheDebit(int boundary)
    {
        failWrite = boundary;
        TestAssert.Throws<IOException>(() => { client.Request(MetadataOperation.Respawn, 2); Finish(); });
        var startsBeforeCrash = starts;
        failWrite = 0;
        Outbound.Clear();
        ReloadWallet();
        client = Restore(false); server = Restore(true);
        Multiplayer.Session.PropertyTransactions = client;
        // A crash lost the local animation; only a durable delivery image can suppress reapplication.
        GameMain.mainPlayer.controller.actionDeath.respawnMode = 0;
        client.GameTick();
        Finish();
        TestAssert.IsFalse(client.Busy);
        var expected = boundary == 1 ? 0 : 12;
        TestAssert.AreEqual(expected, DSPGame.propertySystem.GetItemConsumption(GameMain.data.GetClusterSeedKey(), 6001));
        TestAssert.AreEqual(expected, GameMain.history.GetPropertyItemComsumption(6001));
        TestAssert.AreEqual(boundary == 1 ? 0 : boundary >= 6 ? startsBeforeCrash : startsBeforeCrash + 1, starts);
    }

    private void ReloadWallet()
    {
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(root, "42")));
        TestAssert.AreEqual(0, reader.ReadInt32());
        DSPGame.propertySystem = new PropertySystem();
        AccessTools.Method(typeof(PropertySystem), "Import").Invoke(DSPGame.propertySystem, [reader]);
    }

    private MetadataTransactionManager Restore(bool host)
    {
        var result = Manager(host);
        var values = Transactions(result, host);
        AccessTools.Method(typeof(MetadataTransactionManager), "Load").Invoke(null,
            [Path.Combine(root, host ? "server.bin" : "local.bin"), values]);
        if (host) SetField(result, "sequence", values.Values.Select(x => x.Sequence).DefaultIfEmpty().Max());
        else
        {
            var queue = (Queue<MetadataTransaction>)AccessTools.Field(typeof(MetadataTransactionManager), "requests").GetValue(result);
            foreach (var tx in values.Values)
                if (tx.State is not (MetadataTransactionState.Applied or MetadataTransactionState.Rejected)) queue.Enqueue(tx);
        }
        return result;
    }

    [TestMethod]
    public void LegacyQuotedDebitCanFinishThroughTheNewCommitMessage()
    {
        var tx = new MetadataTransaction
        { Id = Guid.NewGuid().ToString("N"), Owner = "owner", Operation = MetadataOperation.Respawn, Target = 2 };
        var quote = Deliver(new MetadataOperationPacket
        { WorldId = SaveManager.WorldId, Message = MetadataMessage.Request, Transaction = tx.ExportWire() });
        Transactions(client, false).Add(tx.Id, tx);
        client.Receive(quote);
        TestAssert.AreEqual(MetadataMessage.RespawnCommit, Outbound.Peek().Message);
        Finish();
        TestAssert.AreEqual(1, starts);
        TestAssert.IsFalse(client.Busy);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LegacyDebitedJournalKeepsItsQuoteExpiryOnRecovery(bool expired)
    {
        var tx = new MetadataTransaction
        { Id = Guid.NewGuid().ToString("N"), Owner = "owner", Operation = MetadataOperation.Respawn, Target = 2 };
        var quote = Deliver(new MetadataOperationPacket
        { WorldId = SaveManager.WorldId, Message = MetadataMessage.Request, Transaction = tx.ExportWire() });
        Transactions(client, false).Add(tx.Id, tx);
        client.Receive(quote);
        Outbound.Clear();
        AccessTools.Method(typeof(MetadataTransactionManager), "PersistLocal").Invoke(client, null);
        if (expired)
        {
            Transactions(server, true)[tx.Id].Expires = 0;
            AccessTools.Method(typeof(MetadataTransactionManager), "PersistServer").Invoke(server, null);
        }
        client = Restore(false); server = Restore(true);
        client.GameTick();
        Finish();
        TestAssert.AreEqual(expired ? 0 : 1, starts);
        TestAssert.AreEqual(expired ? 0 : 12, DSPGame.propertySystem.GetItemConsumption(GameMain.data.GetClusterSeedKey(), 6001));
        TestAssert.IsFalse(client.Busy);
    }

    [TestMethod]
    public void CostHelperMatchesEveryVanillaDeathTierAndKeepsTheNativeTableUnchanged()
    {
        var costs = (int[][][])AccessTools.Field(typeof(PlayerAction_Death), "respawnCosts").GetValue(null);
        for (var death = 1; death <= costs.Length + 5; death++)
        {
            var options = costs[Math.Min(costs.Length - 1, death - 1)];
            for (var option = 0; option < options.Length; option++)
            {
                CollectionAssert.AreEqual(options[option], RespawnPaymentPolicy.Calculate(death, 3, option, false));
                CollectionAssert.AreEqual(options[option].Select(x => x * 2).ToArray(), RespawnPaymentPolicy.Calculate(death, 2, option, false));
                CollectionAssert.AreEqual(new int[6], RespawnPaymentPolicy.Calculate(death, 2, option, true));
            }
        }
        TestAssert.Throws<InvalidOperationException>(() => RespawnPaymentPolicy.Calculate(1, 1, 0, false));
        TestAssert.Throws<InvalidOperationException>(() => RespawnPaymentPolicy.Calculate(1, 2, 1, false));
    }

    [TestMethod]
    public void StreamingJournalAddsFormatHeaderWithoutChangingTransactionEntries()
    {
        var journal = Transactions(client, false);
        for (var i = 0; i < 3; i++)
        {
            var tx = new MetadataTransaction
            {
                Id = Guid.NewGuid().ToString("N"),
                Owner = "owner",
                Operation = MetadataOperation.Respawn,
                BeforePlayer = Enumerable.Range(0, i * 511).Select(x => (byte)x).ToArray(),
                AfterPlayer = [1, 2, 3]
            };
            journal.Add(tx.Id, tx);
        }
        using var legacy = new MemoryStream();
        using (var writer = new BinaryWriter(legacy, System.Text.Encoding.UTF8, true))
        {
            writer.Write(2); writer.Write(1); writer.Write(journal.Count);
            foreach (var tx in journal.Values)
            {
                var bytes = tx.Export(); writer.Write(bytes.Length); writer.Write(bytes);
            }
        }
        AccessTools.Method(typeof(MetadataTransactionManager), "PersistLocal").Invoke(client, null);
        CollectionAssert.AreEqual(legacy.ToArray(), File.ReadAllBytes(Path.Combine(root, "local.bin")));
    }
}
