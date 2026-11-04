using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class HeadlessAdvisorTest
{
    [TestMethod]
    public void AdvisorKeepsItsManagedLifecycleWithoutOpeningGraphicsOrAudio()
    {
        var harmony = new Harmony("nebulae.tests.headless-advisor");
        var patches = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType(
            "NebulaePatcher.Patches.Misc.Dedicated_Server_Patches", true)!;
        var create = AccessTools.Method(typeof(UIAdvisorTip), "_OnCreate");
        var request = AccessTools.Method(typeof(UIAdvisorTip), "RequestAdvisorTip");
        var run = AccessTools.Method(typeof(UIAdvisorTip), "RunAdvisorTip");
        try
        {
            harmony.Patch(create, prefix: new HarmonyMethod(AccessTools.Method(patches, "UIAdvisorTipCreate_Prefix")));
            var suppress = new HarmonyMethod(AccessTools.Method(patches, "UIAdvisorTipPlay_Prefix"));
            harmony.Patch(request, prefix: suppress);
            harmony.Patch(run, prefix: suppress);
            var advisor = Uninitialized<UIAdvisorTip>();
            create.Invoke(advisor, null);
            // The initialized advisor must support the real _OnFree lifecycle call.
            AccessTools.Method(typeof(UIAdvisorTip), "_OnFree").Invoke(advisor, null);
            request.Invoke(advisor, [1]);
            run.Invoke(advisor, [1]);
            TestAssert.IsNotNull(advisor.requests);
            TestAssert.IsEmpty(advisor.requests);
            TestAssert.IsNull(advisor.playingTip);
            TestAssert.IsNull(AccessTools.Field(typeof(UIAdvisorTip), "audioSampleBuffer").GetValue(advisor));
        }
        finally
        {
            harmony.UnpatchSelf();
        }
    }
}
