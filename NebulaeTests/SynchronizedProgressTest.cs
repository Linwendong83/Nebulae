using System.Runtime.Serialization;
using HarmonyLib;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Context = NebulaePatcher.Patches.Dynamic.UI.SynchronizedProgress.Context;

namespace NebulaeTests;

[TestClass]
public class SynchronizedProgressTest
{
    [TestMethod]
    public void PacketCorrectionHasTheSameResponseAtDifferentRenderRates()
    {
        foreach (var fps in new[] { 30, 60, 144 })
        {
            var value = 0.2f;
            for (var i = 0; i < fps / 2; i++) value = SynchronizedProgress.Ease(value, 0.8f, 1f / fps);
            Check.AreEqual(0.8f, value, 0.0001f);
        }
        var settled = SynchronizedProgress.Ease(0f, 1f, 0.125f);
        Check.AreEqual(0.95f, settled, 0.001f);
    }

    [TestMethod]
    public void RepeatedRefreshesDoNotAccelerateAndSkippedNativeWritesStillConverge()
    {
        var display = new SynchronizedProgress.Display();
        var session = new object();
        var context = new Context(new object(), 1001, 1, 10000);
        Check.AreEqual(0.2f, display.Update(0.2f, context, session, 0, 1f / 60, false));
        var value = display.Update(0.8f, context, session, 1, 1f / 60, false);
        Check.IsTrue(value > 0.2f && value < 0.8f);
        Check.AreEqual(value, display.Update(0.8f, context, session, 1, 1f / 60, false));
        for (var frame = 2; frame < 40; frame++)
            value = display.Update(value, context, session, frame, 1f / 60, false);
        Check.AreEqual(0.8f, value, 0.0001f);
    }

    [TestMethod]
    public void NewResearchLevelTargetSessionAndReopenedWindowSnapToTheirOwnProgress()
    {
        var display = new SynchronizedProgress.Display();
        var session = new object();
        var history = new object();
        var context = new Context(history, 1001, 1, 10000);
        display.Update(0.9f, context, session, 0, 0.016f, true);
        context = new Context(history, 1001, 2, 20000);
        Check.AreEqual(0.01f, display.Update(0.01f, context, session, 1, 0.016f, true));
        context = new Context(history, 1002, 2, 20000);
        Check.AreEqual(0.4f, display.Update(0.4f, context, session, 2, 0.016f, true));
        Check.AreEqual(0.6f, display.Update(0.6f, context, new object(), 3, 0.016f, true));
        Check.AreEqual(0.7f, display.Update(0.7f, context, session, 10, 0.016f, true));
    }

    [TestMethod]
    public void LocalSimulationIsExcludedAndAllPatchesBindToTheInstalledGame()
    {
        var lab = Uninitialized<UILabWindow>();
        var factory = new PlanetFactory { factorySystem = Uninitialized<FactorySystem>() };
        factory.factorySystem.labPool = new LabComponent[2];
        AccessTools.Field(typeof(UILabWindow), "factory").SetValue(lab, factory);
        AccessTools.Field(typeof(UILabWindow), "_labId").SetValue(lab, 1);
        Check.IsFalse(UISynchronizedProgress_Patch.UsesSnapshots(lab));
        factory.factorySystem.labPool[1].researchMode = true;
        Check.IsTrue(UISynchronizedProgress_Patch.UsesSnapshots(lab));
        var row = Uninitialized<UIControlPanelDispenserEntry>();
        Check.IsTrue(UISynchronizedProgress_Patch.UsesSnapshots(row));
        row.factory = factory;
        Check.IsFalse(UISynchronizedProgress_Patch.UsesSnapshots(row));
        var excluded = new[] { typeof(UIAssemblerWindow), typeof(UIMinerWindow), typeof(UIEjectorWindow),
            typeof(UISiloWindow), typeof(UIMechaEnergy), typeof(UIZS_MechaStatBars), typeof(UIMechaWindow) };
        foreach (var binding in UISynchronizedProgress_Patch.Bindings)
            Check.IsFalse(excluded.Contains(binding.Type), binding.Type.Name);

        // Read installed-game IL without JIT-compiling Unity methods in the standalone CLR.
        foreach (var method in UISynchronizedProgress_Patch.TargetMethods())
        {
            Check.IsFalse(method.IsStatic);
            Check.IsTrue(PatchProcessor.GetOriginalInstructions(method).Any(), method.Name);
        }
        foreach (var method in UISynchronizedProgressReset_Patch.TargetMethods())
            Check.AreEqual("_OnOpen", method.Name);
        foreach (var binding in UISynchronizedProgress_Patch.Bindings)
            foreach (var field in binding.Images)
                Check.AreEqual(typeof(UnityEngine.UI.Image), field.FieldType);
        var queue = AccessTools.Method(typeof(UIResearchQueueNode), nameof(UIResearchQueueNode.SetData));
        CollectionAssert.IsSubsetOf(new[] { "researchQueue", "index", "fillAmount" },
            queue.GetParameters().Select(parameter => parameter.Name).ToArray());
    }

    private static T Uninitialized<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
}
