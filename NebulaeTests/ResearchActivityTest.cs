using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.GameDataHistory;
using NebulaeWorld.Statistics;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class ResearchActivityTest
{
    private MultiplayerSession? previousSession;
    private GameData? previousData;
    private object? previousTechs;
    private Harmony harmony = null!;
    private GameStatData statistics = null!;

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session;
        previousData = GameMain.data;
        previousTechs = AccessTools.Field(typeof(LDB), "_techs").GetValue(null);
        var techs = Uninitialized<TechProtoSet>();
        AccessTools.Field(typeof(TechProtoSet), "m_CachedPtr").SetValue(techs, new IntPtr(1));
        techs.dataArray = [new TechProto { ID = 1001, Items = [1001], ItemPoints = [2] }];
        AccessTools.Field(typeof(ProtoSet<TechProto>), "dataIndices").SetValue(techs, new Dictionary<int, int> { [1001] = 0 });
        AccessTools.Field(typeof(LDB), "_techs").SetValue(null, techs);
        statistics = new GameStatData { techHashedHistory = new int[300] };
        GameMain.data = Uninitialized<GameData>();
        GameMain.data.statistics = statistics;
        GameMain.data.history = new GameHistoryData
        {
            gameData = GameMain.data,
            techStates = new Dictionary<int, TechState> { [1001] = new() { hashNeeded = 10000 } }
        };
        AccessTools.PropertySetter(typeof(GameHistoryData), nameof(GameHistoryData.currentTech)).Invoke(GameMain.history, [1001]);
        var session = Uninitialized<MultiplayerSession>();
        session.LocalPlayer = new LocalPlayer { IsHost = false };
        session.Authority = new AuthoritySessionState();
        session.Research = new ResearchContributionManager();
        session.Statistics = Uninitialized<StatisticsManager>();
        Multiplayer.Session = session;
        harmony = new Harmony("nebulae.tests.research-activity");
        harmony.Patch(AccessTools.Method(typeof(GameStatData), nameof(GameStatData.RecordTechHashed)),
            prefix: new HarmonyMethod(typeof(GameStatData_Patch), nameof(GameStatData_Patch.RecordTechHashed_Prefix)));
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchSelf();
        Multiplayer.Session = previousSession;
        GameMain.data = previousData;
        AccessTools.Field(typeof(LDB), "_techs").SetValue(null, previousTechs);
    }

    [TestMethod]
    public void LocalManualResearchRemainsActiveBeforeAnyHostRateArrivesWithoutChangingGlobalProgress()
    {
        for (var tick = 0; tick < 60; tick++)
        {
            statistics.techHashedThisFrame = 0;
            Multiplayer.Session.Research.Record(GameMain.history, 1);
            statistics.RecordTechHashed();
            TestAssert.IsGreaterThan(0, statistics.techHashedFor10Frames,
                "Vanilla uses this window for both the red queue icon and the missing-material message.");
        }
        TestAssert.AreEqual(10, statistics.techHashedFor10Frames);
        TestAssert.AreEqual(0L, GameMain.history.TechState(1001).hashUploaded);
        for (var tick = 0; tick < 10; tick++)
        {
            statistics.techHashedThisFrame = 0;
            statistics.RecordTechHashed();
        }
        TestAssert.AreEqual(0, statistics.techHashedFor10Frames, "Stopping still reaches the native blocked state.");
    }

    [TestMethod]
    public void HostCountsBatchedResearchAfterPrepareTickWithoutGapsOrInflatedSpeed()
    {
        Multiplayer.Session.LocalPlayer.IsHost = true;
        var total = 0;
        for (var tick = 0; tick < 600; tick++)
        {
            if (tick % 30 == 0)
            {
                statistics.techHashedThisFrame += 30; // AddTechHash at the command boundary.
                Multiplayer.Session.Research.DeferCommittedStatistics(statistics, 30);
            }
            statistics.techHashedThisFrame = 0; // Vanilla PrepareTick follows that boundary.
            statistics.RecordTechHashed();
            total += statistics.techHashedThisFrame;
            TestAssert.IsGreaterThan(0, statistics.techHashedFor10Frames);
            TestAssert.IsLessThanOrEqualTo(10, statistics.techHashedFor10Frames);
        }
        TestAssert.AreEqual(600, total);
        for (var tick = 0; tick < 10; tick++)
        {
            statistics.techHashedThisFrame = 0;
            statistics.RecordTechHashed();
        }
        TestAssert.AreEqual(0, statistics.techHashedFor10Frames);
    }

    [TestMethod]
    public void OverlappingPartialBatchesConserveHashesAlongsideHostResearch()
    {
        Multiplayer.Session.LocalPlayer.IsHost = true;
        var total = 0;
        for (var tick = 0; tick < 70; tick++)
        {
            if (tick is 0 or 7 or 30)
            {
                statistics.techHashedThisFrame += 43;
                Multiplayer.Session.Research.DeferCommittedStatistics(statistics, 43);
            }
            statistics.techHashedThisFrame = 2; // Native host mecha/factory work after PrepareTick.
            statistics.RecordTechHashed();
            total += statistics.techHashedThisFrame;
        }
        TestAssert.AreEqual(3 * 43 + 70 * 2, total);
        TestAssert.AreEqual(20, statistics.techHashedFor10Frames, "All deferred batches have drained.");
    }

    [TestMethod]
    public void HostRateAndImmediateLocalWorkAreNotCountedTwice()
    {
        Multiplayer.Session.Statistics.TechHashedFor10Frames = 20;
        statistics.techHashedThisFrame = 1;
        statistics.RecordTechHashed();
        TestAssert.AreEqual(2, statistics.techHashedThisFrame);
        statistics.techHashedThisFrame = 3;
        statistics.RecordTechHashed();
        TestAssert.AreEqual(3, statistics.techHashedThisFrame);
        Multiplayer.Session.Statistics.TechHashedFor10Frames = 0;
        statistics.techHashedThisFrame = 0;
        statistics.RecordTechHashed();
        TestAssert.AreEqual(0, statistics.techHashedThisFrame);
    }
}
