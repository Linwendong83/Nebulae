
using NebulaeModel.Authority;
using NebulaeWorld;
using NebulaeWorld.GameDataHistory;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// Verifies that clients apply host-confirmed tech unlock functions immediately, including drive flight,
/// observation levels, sail speed and warp speed.
/// </summary>
[TestClass]
[DoNotParallelize]
public class TechUnlockFunctionApplyTest
{
    private MultiplayerSession previousSession = null!;

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session;
        ResetGuard();
    }

    [TestCleanup]
    public void TearDown()
    {
        Multiplayer.Session = previousSession;
        ResetGuard();
    }

    [TestMethod]
    public void TechFunctionsApplyOnlyInsideTheAuthorizedWindowOrOnTheHost()
    {
        UseSession(isHost: false);
        TestAssert.IsFalse(GameHistoryData_Patch.UnlockTechFunction_Prefix(),
            "A client outside the unlock window must not decide a host fact.");
        // GameHistoryUnlockTechProcessor and MetadataTransactionManager.ApplyWorld open this window to
        // mirror the host's confirmed unlock, so the effects must land on the client here.
        using (Multiplayer.Session.History.IsIncomingRequest.On())
            TestAssert.IsTrue(GameHistoryData_Patch.UnlockTechFunction_Prefix(),
                "A client must apply the tech functions of a host-confirmed unlock.");

        UseSession(isHost: true);
        TestAssert.IsTrue(GameHistoryData_Patch.UnlockTechFunction_Prefix());

        Multiplayer.Session = null!;
        TestAssert.IsTrue(GameHistoryData_Patch.UnlockTechFunction_Prefix());
    }

    private static void UseSession(bool isHost)
    {
        var session = Uninitialized<MultiplayerSession>();
        session.History = new GameDataHistoryManager();
        session.LocalPlayer = new LocalPlayer { IsHost = isHost };
        Multiplayer.Session = session;
        AuthorityRuleGuard.Probe = () => new AuthorityGuardSession(true, isHost, replicaApplyActive: false);
    }

    private static void ResetGuard()
    {

        AuthorityRuleGuard.ResetVerification();
        AuthorityRuleGuard.ClearVerifiers();
        AuthorityRuleGuard.Probe = null;

    }
}
