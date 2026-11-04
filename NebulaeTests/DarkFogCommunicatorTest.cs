using System.Reflection.Emit;
using HarmonyLib;
using global::NebulaeModel.DataStructures;
using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Combat;
using NebulaeModel.Packets.GameStates;
using NebulaeNetwork.PacketProcessors.Combat;
using NebulaeWorld;
using NebulaeWorld.GameDataHistory;
using NebulaeWorld.GameStates;
using NebulaeWorld.MonoBehaviours.Local.Chat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class DarkFogCommunicatorTest
{
    private readonly Harmony harmony = new("nebulae.tests.darkfog-communicator");
    private MultiplayerSession? previousSession;
    private GameData? previousGame;
    private PropertySystem previousProperties = null!;
    private ChatManager previousChat = null!;
    private bool previousDedicated;
    private string root = "";
    private MetadataTransactionManager client = null!, server = null!;
    private PlayerData local = null!, remote = null!;
    private static readonly Queue<MetadataOperationPacket> Outbound = new();
    private static readonly List<object> Broadcasts = new();
    private static int truceNotifications;

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session; previousGame = GameMain.data;
        previousProperties = DSPGame.propertySystem; previousDedicated = Multiplayer.IsDedicated;
        previousChat = ChatManager.Instance;
        Multiplayer.IsDedicated = false;
        root = Path.Combine(Path.GetTempPath(), "nebulae-communicator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var game = Uninitialized<GameData>();
        game.gameDesc = new GameDesc { starCount = 64, galaxySeed = 123, resourceMultiplier = 1, isPeaceMode = false };
        game.history = History(game, 1f);
        game.spaceSector = Uninitialized<SpaceSector>();
        game.spaceSector.gameData = game;
        game.spaceSector.dfHives = [];
        game.factories = [];
        GameMain.data = game;
        local = new PlayerData(2, 101) { PersistentId = "owner" };
        remote = new PlayerData(2, 101) { PersistentId = "owner", Life = new PlayerLifeData { IsAlive = true } };
        var session = Uninitialized<MultiplayerSession>();
        session.LocalPlayer = new LocalPlayer { Data = local };
        session.IsGameLoaded = true;
        session.Network = new RecordingNetwork();
        session.History = new GameDataHistoryManager();
        session.World = new SimulatedWorld();
        Multiplayer.Session = session;
        DSPGame.propertySystem = new PropertySystem { propertyDatas = [] };
        foreach (var item in PropertySystem.matrixIds)
            DSPGame.propertySystem.SetItemProduction(game.GetClusterSeedKey(), item, 10000);
        client = Manager(false); server = Manager(true);
        session.PropertyTransactions = client;
        ChatManager.Instance = Uninitialized<ChatManager>();
        Outbound.Clear(); Broadcasts.Clear(); truceNotifications = 0;
        Patch(typeof(MetadataManager), "get_LocalIdentity", nameof(Identity));
        Patch(typeof(MetadataTransactionManager), "CapturePlayer", nameof(Capture));
        Patch(typeof(PropertyAccountStore), "Save", nameof(Skip));
        harmony.CreateClassProcessor(typeof(PropertySystem_Patch)).Patch();
        harmony.CreateClassProcessor(typeof(UIDFCommunicatorWindow_Patch)).Patch();
        harmony.Patch(AccessTools.Method(typeof(UIDFCommunicatorWindow_Patch), nameof(UIDFCommunicatorWindow_Patch.Truce_Prefix)),
            transpiler: new HarmonyMethod(typeof(DarkFogCommunicatorTest), nameof(PrivateCommunicatorBoundary)));
        Patch(typeof(UIRealtimeTip), "Popup", nameof(Skip));
        Patch(typeof(ChatManager), "OnMessageSubmitted", nameof(Skip));
        // BepInEx permits publicized game access; the standalone CLR needs this private getter boundary.
        harmony.Patch(AccessTools.Method(typeof(CombatAggressivenessProcessor), "ProcessPacket"),
            transpiler: new HarmonyMethod(typeof(DarkFogCommunicatorTest), nameof(PrivateMultiplierBoundary)));
        harmony.Patch(AccessTools.Method(typeof(SpaceSector), nameof(SpaceSector.OnDFTruce)),
            postfix: new HarmonyMethod(typeof(DarkFogCommunicatorTest), nameof(TruceNotification)));
    }

    private static GameHistoryData History(GameData game, float aggressiveness)
    {
        var history = Uninitialized<GameHistoryData>();
        history.gameData = game;
        history.featureValues = new Dictionary<int, int>();
        history.propertyData = new ClusterPropertyData();
        history.propertyData.Init(game.GetClusterSeedKey());
        history.combatSettings = new CombatSettings();
        history.combatSettings.SetDefault();
        history.combatSettings.aggressiveness = aggressiveness;
        history.minimalDifficulty = history.minimalPropertyMultiplier = 100;
        return history;
    }

    private void Patch(Type type, string name, string prefix, Type? genericArgument = null)
    {
        var method = type == typeof(UIRealtimeTip)
            ? AccessTools.Method(type, name, [typeof(string), typeof(bool), typeof(int)])
            : AccessTools.Method(type, name);
        if (genericArgument != null) method = method.MakeGenericMethod(genericArgument);
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(DarkFogCommunicatorTest), prefix));
    }

    [TestCleanup]
    public void CleanUp()
    {
        harmony.UnpatchSelf();
        Multiplayer.Session = previousSession; GameMain.data = previousGame;
        DSPGame.propertySystem = previousProperties; Multiplayer.IsDedicated = previousDedicated;
        ChatManager.Instance = previousChat;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private MetadataTransactionManager Manager(bool host)
    {
        var result = new MetadataTransactionManager();
        SetField(result, "initialized", true);
        SetField(result, host ? "serverPath" : "localPath", Path.Combine(root, host ? "server.bin" : "local.bin"));
        return result;
    }

    private MetadataOperationPacket Deliver(MetadataOperationPacket packet)
    {
        MetadataOperationPacket? response = null;
        var session = Multiplayer.Session;
        session.Server = new RecordingNetwork();
        session.LocalPlayer.Data = new PlayerData(1, 101);
        try { server.HandleServer(RoundTrip(packet), remote, value => response = RoundTrip(value)); }
        finally { session.Server = null; session.LocalPlayer.Data = local; }
        return response!;
    }

    private void Finish()
    {
        var iterations = 0;
        while (Outbound.Count > 0)
        {
            TestAssert.IsLessThan(10, iterations++, "Transaction did not settle.");
            client.Receive(Deliver(Outbound.Dequeue()));
        }
        TestAssert.IsFalse(client.Busy);
    }

    private static bool Identity(ref string __result) { __result = "owner"; return false; }
    private static bool Capture(ref byte[] __result) { __result = []; return false; }
    private static bool Skip() => false;
    private static void TruceNotification() => truceNotifications++;
    private static int Consumption(int item) => DSPGame.propertySystem.GetItemConsumption(GameMain.data.GetClusterSeedKey(), item);

    private static float Multiplier(GameHistoryData history) =>
        (float)AccessTools.PropertyGetter(typeof(GameHistoryData), "currentPropertyMultiplier").Invoke(history, null);

    private static int PropertyIndex(UIDFCommunicatorWindow window) =>
        (int)AccessTools.Field(typeof(UIDFCommunicatorWindow), "propertyIndex").GetValue(window);
    private static int[] Counts(UIDFCommunicatorWindow window) =>
        (int[])AccessTools.Field(typeof(UIDFCommunicatorWindow), "countOpt").GetValue(window);

    private static IEnumerable<CodeInstruction> PrivateCommunicatorBoundary(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.LoadsField(AccessTools.Field(typeof(UIDFCommunicatorWindow), "propertyIndex")))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(DarkFogCommunicatorTest), nameof(PropertyIndex));
            }
            else if (instruction.LoadsField(AccessTools.Field(typeof(UIDFCommunicatorWindow), "countOpt")))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(DarkFogCommunicatorTest), nameof(Counts));
            }
            yield return instruction;
        }
    }

    private static IEnumerable<CodeInstruction> PrivateMultiplierBoundary(IEnumerable<CodeInstruction> instructions)
    {
        var getter = AccessTools.PropertyGetter(typeof(GameHistoryData), "currentPropertyMultiplier");
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(getter))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(DarkFogCommunicatorTest), nameof(Multiplier));
            }
            yield return instruction;
        }
    }

    private sealed class RecordingNetwork : IServer
    {
        public ushort Port { get; set; }
        public ConcurrentPlayerCollection Players => throw new NotSupportedException();
        public INetPacketProcessor PacketProcessor { get; } = new NebulaeNetPacketProcessor();
        public byte[] WriteOnce<T>(T packet) where T : class, new() => PacketProcessor.Write(packet);
        public void SendPacket<T>(T packet) where T : class, new()
        {
            if (packet is MetadataOperationPacket tx) Outbound.Enqueue(RoundTrip(tx));
            else Broadcasts.Add(RoundTrip(packet));
        }
        public void SendToMatching<T>(T packet, Predicate<INebulaePlayer> condition) where T : class, new() => SendPacket(packet);
        public void SendPacketToLocalStar<T>(T packet) where T : class, new() => SendPacket(packet);
        public void SendPacketToLocalPlanet<T>(T packet) where T : class, new() => SendPacket(packet);
        public void SendPacketToPlanet<T>(T packet, int planetId) where T : class, new() => SendPacket(packet);
        public void SendPacketToStar<T>(T packet, int starId) where T : class, new() => SendPacket(packet);
        public void SendPacketExclude<T>(T packet, INebulaeConnection exclude) where T : class, new() => SendPacket(packet);
        public void SendPacketToStarExclude<T>(T packet, int starId, INebulaeConnection exclude) where T : class, new() => SendPacket(packet);
        public void SendToPlayers<T>(IEnumerable<KeyValuePair<INebulaeConnection, INebulaePlayer>> players, T packet) where T : class, new() => SendPacket(packet);
        public void Disconnect(INebulaeConnection connection, DisconnectionReason reason, string reasonMessage = "") => throw new NotSupportedException();
        public void Start() { }
        public void Update() { }
        public void Dispose() { }
    }

    [TestMethod]
    [DataRow(-1f, true, 0f, 15)]
    [DataRow(0f, true, 0.5f, 15)]
    [DataRow(0.5f, true, 1f, 30)]
    [DataRow(1f, true, 2f, 60)]
    [DataRow(2f, true, 3f, 60)]
    [DataRow(0f, false, -1f, 60)]
    [DataRow(0.5f, false, 0f, 120)]
    [DataRow(1f, false, 0.5f, 180)]
    [DataRow(2f, false, 1f, 180)]
    [DataRow(3f, false, 2f, 180)]
    public void DifficultyButtonsSettleTheQuotedPriceAndBroadcast(float before, bool increase, float after, int cost)
    {
        GameMain.history.combatSettings.aggressiveness = before;
        var window = Uninitialized<UIDFCommunicatorWindow>();
        AccessTools.Method(typeof(UIDFCommunicatorWindow), increase ? "OnAggressiveIncButtonClicked" : "OnAggressiveDecButtonClicked").Invoke(window, [0]);
        TestAssert.AreEqual(before, GameMain.history.combatSettings.aggressiveness, "The button must wait for the host commit.");
        Finish();
        TestAssert.AreEqual(after, GameMain.history.combatSettings.aggressiveness);
        TestAssert.AreEqual(cost, Consumption(6006));
        TestAssert.AreEqual(cost, GameMain.history.GetPropertyItemComsumption(6006));
        TestAssert.IsFalse(GameMain.history.hasUsedPropertyBanAchievement);
        TestAssert.AreEqual(after, Broadcasts.OfType<CombatAggressivenessUpdatePacket>().Single().Aggressiveness);
        TestAssert.AreEqual(1, Broadcasts.OfType<PropertyHistoryPacket>().Count());
    }

    [TestMethod]
    [DataRow(0.5f)]
    [DataRow(1f)]
    [DataRow(2f)]
    [DataRow(3f)]
    public void TruceButtonMatchesVanillaPreviewForEveryMatrix(float aggressiveness)
    {
        GameMain.history.combatSettings.aggressiveness = aggressiveness;
        var window = Uninitialized<UIDFCommunicatorWindow>();
        SetField(window, "history", GameMain.history);
        SetField(window, "standardPropertyCost", new[] { 1, 2, 5, 15, 30, 60 });
        foreach (var count in new[] { 6, 7, 600 })
            for (var index = 0; index < 6; index++)
            {
                SetField(window, "propertyIndex", index);
                var counts = new int[6]; counts[index] = count;
                SetField(window, "countOpt", counts);
                var expected = window.CalculateTimeChange();
                var timer = GameMain.history.dfTruceTimer;
                var debit = Consumption(PropertySystem.matrixIds[index]);
                AccessTools.Method(typeof(UIDFCommunicatorWindow), "OnTruceButtonClicked").Invoke(window, [0]);
                Finish();
                TestAssert.AreEqual(timer + expected, GameMain.history.dfTruceTimer, $"matrix={index} count={count}");
                TestAssert.AreEqual(debit + count, Consumption(PropertySystem.matrixIds[index]));
                var packet = Broadcasts.OfType<CombatTruceUpdatePacket>().Last();
                TestAssert.AreEqual(GameMain.gameTick + timer + expected, packet.TruceEndTime);
            }
        TestAssert.IsFalse(GameMain.history.hasUsedPropertyBanAchievement);
    }

    [TestMethod]
    public void DuplicateCommitDoesNotChargeOrApplyDifficultyTwice()
    {
        client.Request(MetadataOperation.IncreaseAggressiveness);
        client.Receive(Deliver(Outbound.Dequeue()));
        var commit = Outbound.Dequeue();
        var result = Deliver(commit);
        client.Receive(result);
        client.Receive(Deliver(commit));
        Finish();
        TestAssert.AreEqual(2f, GameMain.history.combatSettings.aggressiveness);
        TestAssert.AreEqual(60, Consumption(6006));
        TestAssert.AreEqual(1, Broadcasts.OfType<CombatAggressivenessUpdatePacket>().Count());
    }

    [TestMethod]
    public void DifficultyChangeWhilePaymentIsInFlightRefundsTruce()
    {
        client.Request(MetadataOperation.Truce, 6001, 6);
        client.Receive(Deliver(Outbound.Dequeue()));
        TestAssert.AreEqual(6, Consumption(6001));
        GameMain.history.combatSettings.aggressiveness = 2f;
        Finish();
        TestAssert.AreEqual(0, Consumption(6001));
        TestAssert.AreEqual(0L, GameMain.history.dfTruceTimer);
        TestAssert.IsEmpty(Broadcasts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SandboxDifficultyAndTruceAreFree(bool decrease)
    {
        GameMain.data.gameDesc.isSandboxMode = true;
        client.Request(decrease ? MetadataOperation.DecreaseAggressiveness : MetadataOperation.IncreaseAggressiveness);
        Finish();
        client.Request(MetadataOperation.Truce, 6006, 600);
        Finish();
        TestAssert.IsGreaterThan(0L, GameMain.history.dfTruceTimer);
        TestAssert.AreEqual(0, Consumption(6006));
        TestAssert.AreEqual(0, GameMain.history.GetPropertyItemComsumption(6006));
    }

    [TestMethod]
    public void WithdrawingTruceDoesNotRepeatTheCeasefireSideEffects()
    {
        client.Request(MetadataOperation.Truce, 6001, 6);
        Finish();
        TestAssert.AreEqual(1, truceNotifications);
        AccessTools.Method(typeof(UIDFCommunicatorWindow), "WithdrawTruceConfirm").Invoke(Uninitialized<UIDFCommunicatorWindow>(), null);
        Finish();
        TestAssert.AreEqual(0L, GameMain.history.dfTruceTimer);
        TestAssert.AreEqual(6, Consumption(6001));
        TestAssert.AreEqual(1, truceNotifications, "Withdrawing must not reset hatred and attack waves again.");
    }

    [TestMethod]
    public void RecoveringAnExpiredTruceReceiptDoesNotClearNewAttacks()
    {
        var receipt = new MetadataTransaction
        {
            Id = Guid.NewGuid().ToString("N"),
            Owner = remote.PersistentId,
            Operation = MetadataOperation.Truce,
            State = MetadataTransactionState.Committed,
            Sequence = 1,
            EffectValue = GameMain.gameTick - 60
        };
        var journal = (Dictionary<string, MetadataTransaction>)AccessTools.Field(typeof(MetadataTransactionManager), "serverTransactions").GetValue(server);
        journal.Add(receipt.Id, receipt);
        Deliver(new MetadataOperationPacket
        { WorldId = SaveManager.WorldId, Message = MetadataMessage.Query, Transaction = receipt.ExportWire() });
        TestAssert.AreEqual(0L, GameMain.history.dfTruceTimer);
        TestAssert.AreEqual(0, truceNotifications);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-60L)]
    [DataRow(3600L)]
    public void ClientAppliesTheRemainingTruceWithoutCeasefireOnWithdrawal(long remaining)
    {
        GameMain.history.dfTruceTimer = 600;
        var processor = new NebulaeNetPacketProcessor();
        new CombatTruceUpdateProcessor().Register(processor, false);
        processor.ReadPacket(new NetDataReader(processor.Write(new CombatTruceUpdatePacket(2, GameMain.gameTick + remaining))), null);
        TestAssert.AreEqual(Math.Max(0L, remaining), GameMain.history.dfTruceTimer);
        TestAssert.AreEqual(remaining > 0 ? 1 : 0, truceNotifications);
    }

    [TestMethod]
    public void ClientAppliesDifficultyAndMinimumMultipliersFromTheHost()
    {
        var processor = new NebulaeNetPacketProcessor();
        new CombatAggressivenessProcessor().Register(processor, false);
        processor.ReadPacket(new NetDataReader(processor.Write(new CombatAggressivenessUpdatePacket(2, 0.5f))), null);
        TestAssert.AreEqual(0.5f, GameMain.history.combatSettings.aggressiveness);
        TestAssert.AreEqual(GameMain.history.combatSettings.difficulty, GameMain.history.minimalDifficulty);
        TestAssert.AreEqual(Multiplier(GameMain.history), GameMain.history.minimalPropertyMultiplier);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    public void InvalidCommunicatorOperationsDoNotDebit(int scenario)
    {
        switch (scenario)
        {
            case 0: GameMain.history.combatSettings.aggressiveness = 3f; client.Request(MetadataOperation.IncreaseAggressiveness); break;
            case 1: GameMain.history.combatSettings.aggressiveness = -1f; client.Request(MetadataOperation.DecreaseAggressiveness); break;
            case 2: GameMain.history.combatSettings.aggressiveness = 0f; client.Request(MetadataOperation.Truce, 6001, 6); break;
            case 3: client.Request(MetadataOperation.Truce, 6001, 5); break;
            case 4: client.Request(MetadataOperation.WithdrawTruce); break;
            case 5: GameMain.data.gameDesc.isPeaceMode = true; client.Request(MetadataOperation.IncreaseAggressiveness); break;
            case 6:
                DSPGame.propertySystem.SetItemProduction(GameMain.data.GetClusterSeedKey(), 6006, 0);
                client.Request(MetadataOperation.IncreaseAggressiveness);
                break;
        }
        Finish();
        TestAssert.AreEqual(0, Consumption(6001));
        TestAssert.AreEqual(0, Consumption(6006));
        TestAssert.IsEmpty(Broadcasts);
    }
}
