using System.Reflection;
using System.Reflection.Emit;
using ABN;
using HarmonyLib;
using NebulaeModel;
using NebulaeModel.Attributes;
using NebulaeWorld;
using NebulaeWorld.GameStates;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class MultiplayerAchievementTest
{
    private readonly Harmony harmony = new("nebulae.tests.multiplayer-achievements");
    private MultiplayerSession? previousSession;
    private GameData? previousData;
    private AccountData previousAccount;
    private bool previousDedicated;
    private object? previousPrototypes;
    private AchievementLogic logic = null!;
    private AchievementSystem achievements = null!;
    private int notifications;
    private static int platformReads;
    private static int platformWrites;

    [TestInitialize]
    public void SetUp()
    {
        try
        {
            InitializeFixture();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            TestAssert.Fail(error.ToString());
        }
    }

    private void InitializeFixture()
    {
        previousSession = Multiplayer.Session;
        previousData = GameMain.data;
        previousAccount = AccountData.me;
        previousDedicated = Multiplayer.IsDedicated;
        previousPrototypes = AccessTools.Field(typeof(LDB), "_achievements").GetValue(null);
        Multiplayer.Session = null;
        Multiplayer.IsDedicated = false;
        AccountData.me = new AccountData { platform = ESalePlatform.Standalone, userId = 42 };
        GameMain.data = Uninitialized<GameData>();
        GameMain.data.account = AccountData.me;
        GameMain.data.gameDesc = new GameDesc { achievementEnable = true };
        GameMain.data.history = Uninitialized<GameHistoryData>();
        GameMain.data.abnormalData = new GameAbnormalityData_0925 { runtimeDatas = new AbnormalityRuntimeData[3000] };

        var prototypes = Uninitialized<AchievementProtoSet>();
        SetField(prototypes, "m_CachedPtr", new IntPtr(1));
        prototypes.dataArray = [
            new AchievementProto { ID = 1, TargetValue = 100, ProgressChangeStep = 1,
                PlatformSID = "TEST_LOCKED", SteamStatSID = "TEST_PROGRESS", SyncPlatformProgress = ESyncPlatform.Steam },
            new AchievementProto { ID = 2, TargetValue = 1, ProgressChangeStep = 1, PlatformSID = "TEST_UNLOCKED" }
        ];
        prototypes.OnAfterDeserialize();
        SetStatic(typeof(LDB), "_achievements", prototypes);
        logic = new AchievementLogic();
        SetBackingField(logic, nameof(AchievementLogic.gameData), GameMain.data);
        achievements = new AchievementSystem
        {
            achievements = new Dictionary<int, AchievementState>
            {
                [1] = new AchievementState(1, 100, 25),
                [2] = new AchievementState(2, 1, 1, true, 12)
            },
            onAchievementChange = _ => notifications++
        };
        notifications = platformReads = platformWrites = 0;

        foreach (var patch in new[] { typeof(AchievementLogic_Patch), typeof(AchievementSystem_Patch),
                     typeof(GameAbnormalityData_0925_Patch) })
            harmony.CreateClassProcessor(patch).Patch();
        // Keep real achievement method bodies; replace only the external platform boundary.
        foreach (var method in new[] { "UnlockAchievement", "SetAchievementProgress", "SyncAchievementsToPlatform" })
            harmony.Patch(AccessTools.Method(typeof(AchievementSystem), method),
                transpiler: new HarmonyMethod(typeof(MultiplayerAchievementTest), nameof(ReplacePlatformBoundary)));
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            harmony.UnpatchSelf();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            throw;
        }
        finally
        {
            Multiplayer.Session = previousSession;
            Multiplayer.IsDedicated = previousDedicated;
            GameMain.data = previousData;
            AccountData.me = previousAccount;
            SetStatic(typeof(LDB), "_achievements", previousPrototypes);
        }
    }

    private static IEnumerable<CodeInstruction> ReplacePlatformBoundary(IEnumerable<CodeInstruction> instructions)
    {
        var replacements = new Dictionary<MethodInfo, MethodInfo>
        {
            [AccessTools.Method(typeof(AchievementSystem), "UnlockPlatformAchievement")] = AccessTools.Method(typeof(MultiplayerAchievementTest), nameof(PlatformUnlockBoundary)),
            [AccessTools.Method(typeof(AchievementSystem), "SetPlatformAchievementProgress")] = AccessTools.Method(typeof(MultiplayerAchievementTest), nameof(PlatformProgressBoundary)),
            [AccessTools.PropertyGetter(typeof(PARTNER), nameof(PARTNER.STEAMWORKS))] = AccessTools.Method(typeof(MultiplayerAchievementTest), nameof(SteamAvailable)),
            [AccessTools.PropertyGetter(typeof(PARTNER), nameof(PARTNER.XGPWORKS))] = AccessTools.Method(typeof(MultiplayerAchievementTest), nameof(XgpUnavailable)),
            [AccessTools.Method(typeof(STEAMX), nameof(STEAMX.IsAchievementUnlocked))] = AccessTools.Method(typeof(MultiplayerAchievementTest), nameof(PlatformLocked)),
            [AccessTools.Method(typeof(STEAMX), nameof(STEAMX.GetAchievementProgress))] = AccessTools.Method(typeof(MultiplayerAchievementTest), nameof(PlatformProgress)),
            [AccessTools.Method(typeof(STEAMX), nameof(STEAMX.UnlockAchievement))] = AccessTools.Method(typeof(MultiplayerAchievementTest), nameof(PlatformWrite)),
            [AccessTools.Method(typeof(STEAMX), nameof(STEAMX.SetAchievementProgress))] = AccessTools.Method(typeof(MultiplayerAchievementTest), nameof(PlatformProgressWrite))
        };
        foreach (var instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                instruction.operand is MethodInfo method && replacements.TryGetValue(method, out var replacement))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
            }
            yield return instruction;
        }
    }

    private static bool SteamAvailable() => true;
    private static bool XgpUnavailable() => false;
    private static void PlatformUnlockBoundary(AchievementSystem system, AchievementProto proto)
    {
        TestAssert.IsNotNull(system);
        TestAssert.AreEqual(1, proto.ID);
        platformWrites++;
    }

    private static void PlatformProgressBoundary(AchievementSystem system, AchievementProto proto, long progress)
    {
        TestAssert.IsNotNull(system);
        TestAssert.AreEqual(1, proto.ID);
        TestAssert.AreEqual(50L, progress);
        platformWrites++;
    }

    private static bool PlatformLocked(string id) { TestAssert.StartsWith("TEST_", id); platformReads++; return false; }
    private static int PlatformProgress(string id) { TestAssert.AreEqual("TEST_PROGRESS", id); platformReads++; return 1; }
    private static bool PlatformWrite(string id) { TestAssert.StartsWith("TEST_", id); platformWrites++; return true; }
    private static bool PlatformProgressWrite(string id, int progress)
    {
        TestAssert.AreEqual("TEST_PROGRESS", id);
        TestAssert.AreEqual(25, progress);
        platformWrites++;
        return true;
    }

    private static void EnterSession(string role, bool loaded)
    {
        Multiplayer.IsDedicated = role == "dedicated";
        Multiplayer.Session = Uninitialized<MultiplayerSession>();
        Multiplayer.Session.IsGameLoaded = loaded;
        Multiplayer.Session.LocalPlayer = new LocalPlayer { IsHost = role != "client" };
        if (role != "client") Multiplayer.Session.Server = Uninitialized<global::NebulaeNetwork.Server>();
    }

    [TestMethod]
    [DataRow("host", false)]
    [DataRow("host", true)]
    [DataRow("client", false)]
    [DataRow("client", true)]
    [DataRow("dedicated", false)]
    [DataRow("dedicated", true)]
    public void SessionBlocksDetectionDirectAwardsProgressAndPlatformSync(string role, bool loaded)
    {
        TestAssert.IsTrue(logic.active, "The fixture must be eligible in single player.");
        EnterSession(role, loaded);
        var events = 0;
        logic.onUnlockTech += _ => events++;

        TestAssert.IsFalse(logic.active);
        TestAssert.IsTrue(logic.isSelfFormalGame, "Metadata account compatibility remains available.");
        logic.NotifyOnUnlockTech(1);
        achievements.UnlockAchievement(1, 1000);
        achievements.SetAchievementProgress(1, 100, 1000);
        achievements.SyncAchievementsToPlatform();

        TestAssert.AreEqual(0, events);
        TestAssert.IsFalse(achievements.IsLocalAchievementUnlocked(1));
        TestAssert.AreEqual(25L, achievements.GetLocalAchievementProgressValue(1));
        TestAssert.IsTrue(achievements.IsLocalAchievementUnlocked(2));
        TestAssert.AreEqual(12L, achievements.GetUnlockTick(2));
        TestAssert.AreEqual(0, notifications);
        TestAssert.AreEqual(0, platformReads);
        TestAssert.AreEqual(0, platformWrites);
        TestAssert.IsTrue(GameMain.data.gameDesc.achievementEnable, "No permanent save flag is changed.");
    }

    [TestMethod]
    public void LeavingSessionRestoresNativeUnlockProgressAndPlatformSync()
    {
        EnterSession("client", false);
        achievements.SetAchievementProgress(1, 100, 1000);
        Multiplayer.Session = null;

        TestAssert.IsTrue(logic.active);
        achievements.SetAchievementProgress(1, 50, 2000);
        TestAssert.AreEqual(50L, achievements.GetLocalAchievementProgressValue(1));
        TestAssert.AreEqual(1, platformWrites);
        achievements.UnlockAchievement(1, 3000);
        TestAssert.IsTrue(achievements.IsLocalAchievementUnlocked(1));
        TestAssert.AreEqual(3000L, achievements.GetUnlockTick(1));
        TestAssert.AreEqual(2, notifications);
        TestAssert.AreEqual(2, platformWrites);
        achievements.SyncAchievementsToPlatform();
        TestAssert.AreEqual(4, platformWrites);
    }

    [TestMethod]
    public void SinglePlayerStillRequiresTheOriginalAccountSandboxAndAbnormalityRules()
    {
        TestAssert.IsTrue(logic.active);
        GameMain.data.account.userId++;
        TestAssert.IsFalse(logic.isSelfFormalGame);
        TestAssert.IsFalse(logic.active);
        GameMain.data.account = AccountData.me;
        GameMain.data.gameDesc.isSandboxMode = true;
        TestAssert.IsFalse(logic.active);
        GameMain.data.gameDesc.isSandboxMode = false;
        GameMain.data.gameDesc.achievementEnable = false;
        TestAssert.IsFalse(logic.active);
        GameMain.data.gameDesc.achievementEnable = true;
        GameMain.data.abnormalData.runtimeDatas[30].protoId = 1;
        TestAssert.IsFalse(logic.active);
    }

    [TestMethod]
    public void RealAbnormalityEvidenceStillControlsMetadataEligibility()
    {
        EnterSession("host", true);
        TestAssert.IsTrue(GameMain.data.abnormalData.NothingAbnormal());
        TestAssert.IsFalse(GameMain.data.abnormalData.IsAbnormalTriggerred(1));
        TestAssert.IsTrue(MetadataManager.CanProduce());
        GameMain.data.abnormalData.runtimeDatas[30].protoId = 1;
        TestAssert.IsFalse(GameMain.data.abnormalData.NothingAbnormal());
        TestAssert.IsTrue(GameMain.data.abnormalData.IsAbnormalTriggerred(1));
        TestAssert.IsFalse(MetadataManager.CanProduce());
    }

    [TestMethod]
    public void RemovedConfigKeyCannotBeBoundOrShownAsAnOption()
    {
        var properties = typeof(MultiplayerOptions).GetProperties();
        TestAssert.IsFalse(properties.Any(property => property.Name == "EnableAchievement" ||
            property.GetCustomAttribute<ConfigKeyAttribute>()?.Key == "EnableAchievement"));
    }
}
