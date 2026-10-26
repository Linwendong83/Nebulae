using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.GameStates;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(GameScenarioLogic), nameof(GameScenarioLogic.Init))]
internal static class GoalLevelRestore_Patch
{
    [HarmonyPrefix]
    public static void Prefix(GameScenarioLogic __instance)
    {
        if (Multiplayer.IsActive) GoalManager.RestoreLevel(__instance.gameData);
    }
}

[HarmonyPatch(typeof(GoalLogic))]
internal static class GoalLogic_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GoalLogic.InitDeterminator))]
    [HarmonyPatch(nameof(GoalLogic.GameTick))]
    [HarmonyPatch(nameof(GoalLogic.OnGoalStageChanged))]
    public static bool Authoritative_Prefix() => !Multiplayer.IsActive || Multiplayer.Session.IsServer;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(GoalLogic.CheckGoalLogicActive))]
    public static bool Active_Prefix(GoalLogic __instance, ref bool __result)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        // Retain the UI event bus even though clients do not run authoritative determinators.
        __result = __instance.isInit;
        return false;
    }
}

[HarmonyPatch(typeof(GoalSystem), nameof(GoalSystem.GetGoalDataById))]
internal static class PersonalGoalData_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(int _protoId, ref GoalData __result)
    {
        if (!Multiplayer.IsActive) return true;
        var goals = Multiplayer.Session.Goals;
        if (goals.CollectingPersonal)
        {
            __result = goals.GetPersonalGoal(_protoId);
            return false;
        }
        if (!goals.RenderingPersonal) return true;
        GameMain.data.goalSystem.goalDatas.TryGetValue(_protoId, out var shared);
        __result = goals.GetDisplayGoal(_protoId, shared);
        return false;
    }
}

[HarmonyPatch(typeof(GoalData), nameof(GoalData.stage), MethodType.Setter)]
internal static class GoalStage_Patch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsServer) Multiplayer.Session.Goals.Dirty = true;
    }
}

[HarmonyPatch(typeof(DeterminatorBase), nameof(DeterminatorBase.GameTick))]
internal static class HeadlessGoalDeterminator_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(DeterminatorBase __instance)
    {
        return !Multiplayer.IsActive || !Multiplayer.Session.IsDedicated ||
            __instance is not GoalDeterminator || !GoalManager.PersonalDeterminators.Contains(__instance.GetType().Name);
    }
}

[HarmonyPatch(typeof(GoalDeterminator))]
internal static class HeadlessGoalLoad_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GoalDeterminator.DetermineOnLoad))]
    [HarmonyPatch(nameof(GoalDeterminator.DetermineOnPatch))]
    public static bool Prefix(GoalDeterminator __instance) => !Multiplayer.IsActive || !Multiplayer.Session.IsDedicated ||
        !GoalManager.PersonalDeterminators.Contains(__instance.GetType().Name);
}

[HarmonyPatch(typeof(UIGoalSetting), nameof(UIGoalSetting.OnGoalButtonClick))]
internal static class SharedGoalSetting_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(UIGoalSetting __instance, int _data)
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.Goals.AwaitingSelection)
        {
            Multiplayer.Session.Goals.SelectInitialLevel(_data, __instance);
            return false;
        }
        if (__instance.galaxySelect.active || __instance.loadGameWindow.active) return true;
        Multiplayer.Session.Goals.ChangePersonalLevel(_data);
        __instance.CloseSettingWindow();
        return false;
    }
}

[HarmonyPatch(typeof(UIGoalSetting), nameof(UIGoalSetting.OnGoalButtonClick))]
internal static class EditorGoalPick_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(UIGoalSetting __instance, int _data)
    {
        // The stock handler drops the choice while no game is running, which is exactly
        // the server editor's situation: route it into the editor's combo instead.
        if (!MultiplayerPage.PickingGoalLevel) return true;
        MultiplayerPage.SetEditorGoalLevel(_data);
        __instance.CloseSettingWindow();
        return false;
    }
}

[HarmonyPatch(typeof(UILoadGameWindow), nameof(UILoadGameWindow.OnComboBoxClick))]
internal static class EditorGoalPageToFront_Patch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        // The cloned combo still opens the picker through the load window's own handler,
        // but the picker lives under Top Windows, behind the always-last multiplayer page.
        if (MultiplayerPage.PickingGoalLevel) MultiplayerPage.BringGoalSettingToFront();
    }
}

[HarmonyPatch(typeof(UIGoalSetting), nameof(UIGoalSetting.OnQuitButtonClick))]
internal static class RequiredGoalChoice_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(UIGoalSetting __instance)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.Goals.AwaitingSelection) return true;
        __instance.CloseSettingWindow();
        // The world has not started yet, so leaving must bring the join menu back up.
        Multiplayer.ShouldReturnToJoinMenu = true;
        Multiplayer.LeaveGame();
        return false;
    }
}

[HarmonyPatch(typeof(NebulaeWorld.GameStates.GoalManager), nameof(NebulaeWorld.GameStates.GoalManager.PrepareClientProfile))]
internal static class GoalPromptUi_Patch
{
    [HarmonyPostfix]
    public static void Postfix(bool __result)
    {
        // A false result means the goal choice window is up; the join menu must never
        // render on top of it, whatever connect path led here.
        if (!__result) MultiplayerPage.HideForConnection();
    }
}

[HarmonyPatch(typeof(UIGoalPanel))]
internal static class PersonalGoalPanel_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch("_OnOpen")]
    [HarmonyPatch("_OnUpdate")]
    [HarmonyPatch(nameof(UIGoalPanel.Determine))]
    [HarmonyPatch(nameof(UIGoalPanel.DetermineVisiable))]
    [HarmonyPatch(nameof(UIGoalPanel.DetermineEntry))]
    [HarmonyPatch(nameof(UIGoalPanel.DetermineTransform))]
    public static void Prefix()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Goals.BeginUiRender();
    }

    [HarmonyPostfix]
    [HarmonyPatch("_OnOpen")]
    [HarmonyPatch("_OnUpdate")]
    [HarmonyPatch(nameof(UIGoalPanel.Determine))]
    [HarmonyPatch(nameof(UIGoalPanel.DetermineVisiable))]
    [HarmonyPatch(nameof(UIGoalPanel.DetermineEntry))]
    [HarmonyPatch(nameof(UIGoalPanel.DetermineTransform))]
    public static void Postfix()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Goals.EndUiRender();
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIGoalPanel.OnSettingButtonClick))]
    public static void SetPersonalOpeningLevel()
    {
        if (Multiplayer.IsActive) UIRoot.instance.goalSetting.SetOpeningGoalLevel(Multiplayer.Session.Goals.PersonalLevel);
    }
}

[HarmonyPatch(typeof(GoalSystem), nameof(GoalSystem.IsGoalQueueEmpty))]
internal static class PersonalGoalQueue_Patch
{
    [HarmonyPostfix]
    public static void Postfix(ref bool __result)
    {
        if (!Multiplayer.IsActive) return;
        var goals = Multiplayer.Session.Goals;
        // Vanilla waits for the entries to leave and the panel to collapse before
        // closing an empty queue. Keep that animation without clearing shared goals.
        if (goals.RenderingPersonal && goals.PersonalLevel == EGoalLevel.Off) __result = true;
    }
}

[HarmonyPatch(typeof(UIGoalGroupEntry))]
internal static class PersonalGoalGroupEntry_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIGoalGroupEntry.DetermineEntry))]
    [HarmonyPatch(nameof(UIGoalGroupEntry.DetermineTransform))]
    public static void Prefix()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Goals.BeginUiRender();
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIGoalGroupEntry.DetermineEntry))]
    [HarmonyPatch(nameof(UIGoalGroupEntry.DetermineTransform))]
    public static void Postfix()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Goals.EndUiRender();
    }
}

[HarmonyPatch(typeof(UIGoalInfoEntry), nameof(UIGoalInfoEntry.DetermineTransform))]
internal static class PersonalGoalInfoEntry_Patch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Goals.BeginUiRender();
    }

    [HarmonyPostfix]
    public static void Postfix()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Goals.EndUiRender();
    }
}

[HarmonyPatch(typeof(UIGoalGroupEntry), nameof(UIGoalGroupEntry.OnClickIgnoreButton))]
internal static class SharedGoalIgnore_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(UIGoalGroupEntry __instance)
    {
        if (!Multiplayer.IsActive) return true;
        Multiplayer.Session.Goals.IgnorePersonalGoal(__instance.protoId);
        return false;
    }
}

[HarmonyPatch(typeof(UIGoalInfoEntry), nameof(UIGoalInfoEntry.OnClickIgnoreButton))]
internal static class PersonalGoalInfoIgnore_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(UIGoalInfoEntry __instance)
    {
        if (!Multiplayer.IsActive) return true;
        Multiplayer.Session.Goals.IgnorePersonalGoal(__instance.protoId);
        return false;
    }
}

[HarmonyPatch(typeof(GoalTools), nameof(GoalTools.ItemPlayerStorageCount))]
internal static class SharedGoalInventory_Patch
{
    [HarmonyPostfix]
    public static void Postfix(int _itemId, ref long __result)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsServer) return;
        if (Multiplayer.Session.IsDedicated) __result = 0;
        foreach (var player in Multiplayer.Session.Server.Players.Connected.Values)
        {
            var mecha = player.Data.Mecha;
            long count = mecha.Inventory?.GetItemCount(_itemId) ?? 0;
            if (mecha.DeliveryPackage?.grids != null)
                foreach (var grid in mecha.DeliveryPackage.grids)
                    if (grid.itemId == _itemId) count += grid.count;
            __result = System.Math.Max(__result, count);
        }
    }
}

[HarmonyPatch(typeof(GoalTools), nameof(GoalTools.GetMechaWarpStorage))]
internal static class SharedGoalWarpStorage_Patch
{
    [HarmonyPostfix]
    public static void Postfix(ref long __result)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsServer) return;
        if (Multiplayer.Session.IsDedicated) __result = 0;
        foreach (var player in Multiplayer.Session.Server.Players.Connected.Values)
            __result = System.Math.Max(__result, player.Data.Mecha.WarpStorage?.GetItemCount(1210) ?? 0);
    }
}
