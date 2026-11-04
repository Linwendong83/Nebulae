using HarmonyLib;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class FrameRenderingTest
{
    private static int updates, draws;
    private static bool Update() { updates++; return false; }
    private static bool Draw() { draws++; return false; }

    [TestMethod]
    public void DetachedRenderPoolsSleepWhenEmptyAndResumeForTheNextEffect()
    {
        var previous = GameMain.data;
        var harmony = new Harmony("nebulae.tests.idle-render-pools");
        try
        {
            GameMain.data = null;
            updates = draws = 0;
            harmony.Patch(AccessTools.Method(typeof(SkillSystem), nameof(SkillSystem.RendererUpdate)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(FrameRenderingTest), nameof(Update))));
            harmony.Patch(AccessTools.Method(typeof(SkillSystem), nameof(SkillSystem.RendererDraw)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(FrameRenderingTest), nameof(Draw))));
            var visuals = Uninitialized<SkillSystem>();
            visuals.mechaMissileTrails = new VFTrailRenderer { trailCursor = 1 };
            visuals.hitEffects = new DataPoolRenderer<ParticleData>[10];
            var renderer = Uninitialized<BattleVisualRenderer>();
            AccessTools.Field(typeof(BattleVisualRenderer), "visuals").SetValue(renderer, visuals);
            for (var frame = 0; frame < 1000; frame++) renderer.DrawPools(false);
            TestAssert.AreEqual(0, updates);
            TestAssert.AreEqual(0, draws);
            renderer.DrawPools(true);
            TestAssert.AreEqual(1, updates);
            TestAssert.AreEqual(1, draws);
            for (var frame = 0; frame < 1000; frame++) renderer.DrawPools(false);
            TestAssert.AreEqual(1, updates);
            TestAssert.AreEqual(1, draws);
            renderer.DrawPools(true);
            TestAssert.AreEqual(2, updates);
            TestAssert.AreEqual(2, draws);
        }
        finally { harmony.UnpatchSelf(); GameMain.data = previous; }
    }
}
