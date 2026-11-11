using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine.UI;
using Context = NebulaePatcher.Patches.Dynamic.UI.SynchronizedProgress.Context;

namespace NebulaePatcher.Patches.Dynamic.UI;

// Explicitly opt in host-synchronized meters. Do not globally patch Image.fillAmount:
// loading animations, click/hold indicators and settings sliders have different semantics.
[HarmonyPatch]
internal static class UISynchronizedProgress_Patch
{
    internal sealed class Binding
    {
        internal Type Type;
        internal string[] Methods;
        internal FieldInfo[] Images;
        internal Func<object, Context> Context;

        internal void Render(object owner, Context context, bool reset = false)
        {
            foreach (var field in Images)
            {
                var image = (Image)field.GetValue(owner);
                if (reset) SynchronizedProgress.Reset(image);
                else SynchronizedProgress.Apply(image, context);
            }
        }
    }

    private static readonly AccessTools.FieldRef<UILabWindow, PlanetFactory> labFactory = AccessTools.FieldRefAccess<UILabWindow, PlanetFactory>("factory");
    private static readonly AccessTools.FieldRef<UIControlPanelStationEntry, PlanetFactory> stationFactory = AccessTools.FieldRefAccess<UIControlPanelStationEntry, PlanetFactory>("factory");
    private static readonly AccessTools.FieldRef<UIControlPanelAdvancedMinerEntry, PlanetFactory> minerFactory = AccessTools.FieldRefAccess<UIControlPanelAdvancedMinerEntry, PlanetFactory>("factory");
    private static readonly AccessTools.FieldRef<UIChartTech, TechStatPlan> techPlan = AccessTools.FieldRefAccess<UIChartTech, TechStatPlan>("statPlan");

    internal static readonly Binding[] Bindings =
    {
        Bind<UITechNode>("UpdateInfoDynamic|UpdateInfoComplete", u => SynchronizedProgress.Research(u.techProto?.ID ?? 0), "progressBar"),
        Bind<UITechTip>("UpdateInfoDynamic|UpdateInfoComplete", u => SynchronizedProgress.Research(u.techProto?.ID ?? 0, u.levelIncreasement), "progressBar"),
        Bind<UIChartTech>("Refresh|_OnUpdate", u => SynchronizedProgress.Research(techPlan(u)?.currentTech ?? 0), "techProgressImage"),
        // Matrix production remains local; only the research mode consumes host hash snapshots.
        Bind<UILabWindow>("_OnUpdate", LabContext, "productPercent"),
        // Turret inventory is corrected by TurretStatePolicy every 120 ticks. Firing clocks
        // and the supernova countdown advance locally and deliberately keep native rendering.
        Bind<UITurretWindow>("_OnUpdate", u => new Context(u.factory, u.turretId,
            u.factory != null && u.turretId > 0 ? u.factory.defenseSystem.turrets.buffer[u.turretId].itemId : 0),
            "ammoIndicatorBg", "ammoIndicatorFront", "ammoIndicatorExtra"),
        // Loaded-factory rows run native power simulation. Only remote rows read LCP packets.
        Bind<UIControlPanelStationEntry>("_OnUpdate", u => RowContext(u), "powerCircleFg", "powerRoundFg"),
        Bind<UIControlPanelDispenserEntry>("_OnUpdate", u => RowContext(u), "powerCircleFg", "powerRoundFg"),
        Bind<UIControlPanelAdvancedMinerEntry>("_OnUpdate", u => RowContext(u), "powerCircleFg", "powerRoundFg"),
        Bind<UIDarkFogMonitorEntry>("RefreshValuesComplete", u => u.targetBase != null
            ? new Context(u.targetBase, variant: u.targetBase.evolve.level)
            : new Context(u.targetHive, variant: u.targetHive?.evolve.level ?? 0), "expBar"),
        Bind<UIEnemyBriefInfo>("_OnUpdate", u => new Context(u.sector, u.astroId, u.enemyId, u.enemyInfo?.builderState ?? 0),
            "progressImage", "progress2Image")
    };

    private static Binding Bind<T>(string methods, Func<T, Context> context, params string[] fields) => new Binding
    {
        Type = typeof(T),
        Methods = methods.Split('|'),
        Context = owner => context((T)owner),
        Images = Array.ConvertAll(fields, name => AccessTools.Field(typeof(T), name) ??
            throw new MissingFieldException(typeof(T).Name, name))
    };

    private static Context LabContext(UILabWindow ui)
    {
        var research = SynchronizedProgress.Research(GameMain.history.currentTech);
        return new Context(labFactory(ui), ui.labId, research.Id, research.Variant);
    }

    private static Context RowContext(UIControlPanelObjectEntry row) =>
        new(GameMain.history, row.target.astroId, row.target.objId);

    internal static bool UsesSnapshots(object owner) => owner switch
    {
        UILabWindow lab => labFactory(lab) != null && lab.labId > 0 &&
            labFactory(lab).factorySystem.labPool[lab.labId].researchMode,
        UIControlPanelStationEntry row => stationFactory(row) == null,
        UIControlPanelDispenserEntry row => row.factory == null,
        UIControlPanelAdvancedMinerEntry row => minerFactory(row) == null,
        _ => true
    };

    internal static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var binding in Bindings)
            foreach (var name in binding.Methods)
                yield return AccessTools.DeclaredMethod(binding.Type, name) ?? throw new MissingMethodException(binding.Type.Name, name);
    }

    [HarmonyPostfix]
    internal static void Postfix(object __instance, MethodBase __originalMethod)
    {
        foreach (var binding in Bindings)
        {
            if (binding.Type != __originalMethod.DeclaringType) continue;
            if (!NebulaeWorld.Authority.WorldSimulation.IsClient || !UsesSnapshots(__instance))
            {
                binding.Render(__instance, default, reset: true);
                return;
            }
            var context = binding.Context(__instance);
            binding.Render(__instance, context);
            // Native positions the endpoint using the unsmoothed research ratio.
            if (__instance is UITechNode node) AlignDot(node.progressBar, node.progressDot);
            else if (__instance is UITechTip tip) AlignDot(tip.progressBar, tip.progressDot);
            return;
        }
    }

    private static void AlignDot(Image bar, Image dot)
    {
        if (bar == null || dot == null) return;
        var position = dot.rectTransform.anchoredPosition;
        position.x = bar.fillAmount * bar.rectTransform.rect.width;
        dot.rectTransform.anchoredPosition = position;
    }
}

[HarmonyPatch]
internal static class UISynchronizedProgressReset_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var binding in UISynchronizedProgress_Patch.Bindings)
        {
            var method = AccessTools.DeclaredMethod(binding.Type, "_OnOpen");
            if (method != null) yield return method;
        }
    }

    [HarmonyPrefix]
    internal static void Prefix(object __instance, MethodBase __originalMethod)
    {
        foreach (var binding in UISynchronizedProgress_Patch.Bindings)
            if (binding.Type == __originalMethod.DeclaringType)
                binding.Render(__instance, default, reset: true);
    }
}

[HarmonyPatch(typeof(UIResearchQueueNode), nameof(UIResearchQueueNode.SetData))]
internal static class UIResearchQueueProgress_Patch
{
    [HarmonyPostfix]
    internal static void Postfix(UIResearchQueueNode __instance, int[] researchQueue, int index, double fillAmount)
    {
        // Use the actual input even when vanilla's 0.6% threshold skips an image write.
        SynchronizedProgress.Apply(__instance.progressImage, SynchronizedProgress.Research(researchQueue[index]), (float)fillAmount);
    }
}
