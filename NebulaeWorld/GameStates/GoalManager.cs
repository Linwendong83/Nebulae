using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NebulaeModel;
using NebulaeModel.DataStructures;
using NebulaeModel.Logger;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Utils;
using NebulaeModel.Packets.GameStates;

namespace NebulaeWorld.GameStates;

internal sealed class GoalManager : IDisposable
{
    private const int GoalLevelKey = -1708469030;
    public static EGoalLevel PendingHostLevel { get; set; } = EGoalLevel.None;
    public static void RestoreLevel(GameData data)
    {
        if (!Multiplayer.Session.IsServer) return;
        var manager = Multiplayer.Session.Goals;
        if (!manager.legacyCaptured)
        {
            var level = data.gameDesc.goalLevel;
            if (level == EGoalLevel.None)
            {
                data.history.featureValues.TryGetValue(GoalLevelKey, out var oldLevel);
                level = oldLevel >= (int)EGoalLevel.Off && oldLevel <= (int)EGoalLevel.Full
                    ? (EGoalLevel)oldLevel : EGoalLevel.Full;
            }
            manager.legacyLevel = level;
            if (data.goalSystem != null)
            {
                foreach (var goal in data.goalSystem.goalDatas.Values)
                {
                    if (goal.isManualIgnore) manager.legacyIgnored.Add(goal.protoId);
                    if (goal.stage == EGoalStage.Ignored)
                    {
                        goal.isManualIgnore = false;
                        goal.stage = EGoalStage.Enabled;
                    }
                }
            }
            manager.legacyCaptured = true;
        }
        data.gameDesc.goalLevel = EGoalLevel.Full;
        data.history.featureValues[GoalLevelKey] = (int)EGoalLevel.Full;
    }
    public static readonly HashSet<string> PersonalDeterminators = new(StringComparer.Ordinal)
    {
        "GD_CameraRotate", "GD_ControlorMove", "GD_MechaFuelUsed", "GD_ReplenishMechaAmmo",
        "GD_CheckGroundSquadron", "GD_CheckSpaceSquadron", "GD_CreateBlueprint", "GD_UseBlueprint",
        "GD_SailOrbitCheck", "GD_StarTravelledCheck", "GD_SpaceCapsuleRecycled", "GD_MechaAndFactoryItemProduction"
    };
    private readonly Dictionary<int, GoalData> personalData = new();
    private readonly Dictionary<int, GoalData> displayData = new();
    private readonly HashSet<int> localIgnored = new();
    private readonly HashSet<int> legacyIgnored = new();
    private readonly Dictionary<int, GoalDeterminator> collectors = new();
    private readonly Dictionary<int, long> sentProgress = new();
    private readonly Dictionary<(ushort Player, int Goal), (long Sequence, long Value)> observed = new();
    private byte[] lastSnapshot;
    private long version;
    private long observationSequence;
    private long lastTick = -1;
    private bool snapshotReady;
    private bool legacyCaptured;
    private bool existingPlayer;
    private bool hasPersonalProfile;
    private bool awaitingSelection;
    private int uiDepth;
    private EGoalLevel savedWorldLevel;
    private EGoalLevel legacyLevel = EGoalLevel.Full;
    private EGoalLevel localLevel = EGoalLevel.Full;
    private Action continueAfterSelection;
    public bool CollectingPersonal { get; private set; }
    public bool Applying { get; private set; }
    public bool Dirty { get; set; }
    public bool RenderingPersonal => uiDepth > 0;
    public bool AwaitingSelection => awaitingSelection;
    public EGoalLevel PersonalLevel => localLevel;

    public void SetExistingPlayer(bool value) => existingPlayer = value;

    public void PrepareHostProfile()
    {
        if (!Multiplayer.Session.IsServer || Multiplayer.IsDedicated) return;
        var profile = ServerMemoryStore.Instance.FindProfile(null, SaveManager.WorldId,
            CryptoUtils.GetCurrentUserPublicKeyHash());
        if (profile != null) LoadProfile(profile);
        else
        {
            localLevel = PendingHostLevel is >= EGoalLevel.Off and <= EGoalLevel.Full
                ? PendingHostLevel : legacyLevel;
            localIgnored.UnionWith(legacyIgnored);
            hasPersonalProfile = true;
        }
        if (PendingHostLevel is >= EGoalLevel.Off and <= EGoalLevel.Full)
            localLevel = PendingHostLevel;
        SavePersonalProfile();
        PendingHostLevel = EGoalLevel.None;
    }

    public bool PrepareClientProfile(Action continueLoading)
    {
        var connection = Multiplayer.LastConnection;
        PersonalGoalProfile profile = null;
        if (!string.IsNullOrEmpty(connection?.RecordId))
            profile = ServerMemoryStore.Instance.FindProfile(connection.RecordId, SaveManager.WorldId,
                CryptoUtils.GetCurrentUserPublicKeyHash());
        else if (connection?.TransientWorldId == SaveManager.WorldId)
            profile = connection.TransientGoals;
        if (profile != null)
        {
            LoadProfile(profile);
            return true;
        }
        var recordLevel = GetSavedServerLevel();
        if (recordLevel >= 0)
        {
            // The level picked for this server on the multiplayer page: matching the
            // load-game window, the stored choice applies without asking again.
            localLevel = (EGoalLevel)recordLevel;
            if (existingPlayer) localIgnored.UnionWith(legacyIgnored);
            hasPersonalProfile = true;
            SavePersonalProfile();
            return true;
        }
        awaitingSelection = true;
        continueAfterSelection = continueLoading;
        UIRoot.instance.CloseLoadingUI();
        NebulaeWorld.InGamePopup.FadeOut();
        var setting = UIRoot.instance.goalSetting;
        setting._Open();
        setting.SetOpeningGoalLevel(legacyLevel);
        setting.transform.SetAsLastSibling();
        return false;
    }

    private int GetSavedServerLevel()
    {
        var recordId = Multiplayer.LastConnection?.RecordId;
        if (string.IsNullOrEmpty(recordId)) return -1;
        var level = ServerMemoryStore.Instance.FindServer(recordId)?.GoalLevel ?? 0;
        return level >= (int)EGoalLevel.Off && level <= (int)EGoalLevel.Full ? level : -1;
    }

    private void SyncServerRecordLevel(int level)
    {
        // Hosts keep their level in lobby state; only clients have a server record.
        if (!Multiplayer.Session.IsClient) return;
        if (level < (int)EGoalLevel.Off || level > (int)EGoalLevel.Full) return;
        try { ServerMemoryStore.Instance.UpdateGoalLevel(Multiplayer.LastConnection?.RecordId, level); }
        catch (Exception e) { Log.Warn($"Could not save server goal level: {e.Message}"); }
    }

    public void SelectInitialLevel(int level, UIGoalSetting setting)
    {
        if (!awaitingSelection || level < (int)EGoalLevel.Off || level > (int)EGoalLevel.Full) return;
        localLevel = (EGoalLevel)level;
        if (existingPlayer) localIgnored.UnionWith(legacyIgnored);
        hasPersonalProfile = true;
        SavePersonalProfile();
        SyncServerRecordLevel(level);
        awaitingSelection = false;
        setting.CloseSettingWindow();
        UIRoot.instance.OpenLoadingUI();
        var callback = continueAfterSelection;
        continueAfterSelection = null;
        callback?.Invoke();
    }

    public void ChangePersonalLevel(int level)
    {
        if (level < (int)EGoalLevel.Off || level > (int)EGoalLevel.Full) return;
        localLevel = (EGoalLevel)level;
        if (Multiplayer.Session.IsClient && GameMain.data != null) GameMain.data.gameDesc.goalLevel = localLevel;
        SavePersonalProfile();
        SyncServerRecordLevel(level);
        GameMain.gameScenario?.goalLogic?.NotifyOnGoalLevelChanged();
        UIRoot.instance?.uiGame?.goalPanel?.Reset();
    }

    public void IgnorePersonalGoal(int id)
    {
        if (id <= 0 || GameMain.data?.goalSystem?.GetGoalDataById(id)?.stage == EGoalStage.Completed) return;
        localIgnored.Add(id);
        SavePersonalProfile();
        UIRoot.instance?.uiGame?.goalPanel?.Reset();
    }

    public GoalData GetDisplayGoal(int id, GoalData shared)
    {
        if (!RenderingPersonal || shared == null) return shared;
        if (!displayData.TryGetValue(id, out var data))
            displayData[id] = data = new GoalData(id);
        SyncDisplayGoal(id, shared, data);
        return data;
    }

    private void SyncDisplayGoal(int id, GoalData shared, GoalData data)
    {
        data._stage = localIgnored.Contains(id) && shared.stage != EGoalStage.Completed
            ? EGoalStage.Ignored : shared.stage;
        data.currentValue = shared.currentValue;
        data.targetValue = shared.targetValue;
        data.isManualIgnore = localIgnored.Contains(id);
        data.isPatched = shared.isPatched;
        data.currentIgnoreLevel = shared.currentIgnoreLevel;
    }

    public void BeginUiRender()
    {
        if (uiDepth++ != 0 || GameMain.data?.gameDesc == null) return;
        savedWorldLevel = GameMain.data.gameDesc.goalLevel;
        GameMain.data.gameDesc.goalLevel = localLevel;
    }

    public void EndUiRender()
    {
        if (uiDepth == 0 || --uiDepth != 0 || GameMain.data?.gameDesc == null) return;
        GameMain.data.gameDesc.goalLevel = savedWorldLevel;
    }

    private void LoadProfile(PersonalGoalProfile profile)
    {
        localLevel = profile.Level >= (int)EGoalLevel.Off && profile.Level <= (int)EGoalLevel.Full
            ? (EGoalLevel)profile.Level : EGoalLevel.Full;
        localIgnored.Clear();
        if (profile.IgnoredGoalIds != null) localIgnored.UnionWith(profile.IgnoredGoalIds);
        hasPersonalProfile = true;
    }

    private void SavePersonalProfile()
    {
        if (!hasPersonalProfile) return;
        var recordId = Multiplayer.Session.IsServer ? null : Multiplayer.LastConnection?.RecordId;
        var profile = new PersonalGoalProfile
        { Level = (int)localLevel, IgnoredGoalIds = localIgnored.OrderBy(x => x).ToList() };
        if (Multiplayer.Session.IsServer || !string.IsNullOrEmpty(recordId))
        {
            try
            {
                ServerMemoryStore.Instance.SaveProfile(recordId, SaveManager.WorldId,
                    CryptoUtils.GetCurrentUserPublicKeyHash(), profile.Level, profile.IgnoredGoalIds);
            }
            catch (Exception e) { Log.Warn($"Could not save personal goals: {e.Message}"); }
        }
        else if (Multiplayer.LastConnection != null)
        {
            Multiplayer.LastConnection.TransientWorldId = SaveManager.WorldId;
            Multiplayer.LastConnection.TransientGoals = profile;
        }
    }

    public void LoadLegacyDefaults()
    {
        if (!Multiplayer.Session.IsServer) return;
        var path = Path.Combine(LegacyDataMigration.DataDirectory(GameConfig.gameSaveFolder), SaveManager.WorldId, "goal-defaults.bin");
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                using var stream = File.OpenRead(candidate);
                using var reader = new BinaryReader(stream);
                var level = reader.ReadInt32();
                var count = reader.ReadInt32();
                if (count < 0 || count > LDB.goals.Length) throw new InvalidDataException("Invalid goal defaults");
                var ids = new HashSet<int>();
                for (var i = 0; i < count; i++) ids.Add(reader.ReadInt32());
                legacyLevel = level >= (int)EGoalLevel.Off && level <= (int)EGoalLevel.Full
                    ? (EGoalLevel)level : EGoalLevel.Full;
                legacyIgnored.Clear();
                legacyIgnored.UnionWith(ids);
                return;
            }
            catch (Exception e) { Log.Warn($"Could not load {candidate}: {e.Message}"); }
        }
        if (File.Exists(path)) return; // Keep damaged data for manual recovery.
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
        {
            writer.Write((int)legacyLevel);
            writer.Write(legacyIgnored.Count);
            foreach (var id in legacyIgnored.OrderBy(x => x)) writer.Write(id);
        }
        try { AtomicFile.Write(path, output.ToArray()); }
        catch (Exception e) { Log.Warn($"Could not save goal defaults: {e.Message}"); }
    }

    public void ReadDefaults(byte[] bytes)
    {
        var snapshot = GoalSnapshot.Read(bytes, LDB.goals.Length, LDB.goals.Length);
        legacyLevel = snapshot.DefaultLevel;
        legacyIgnored.Clear();
        legacyIgnored.UnionWith(snapshot.IgnoredGoals);
    }

    public GoalData GetPersonalGoal(int id)
    {
        if (!personalData.TryGetValue(id, out var data))
            personalData[id] = data = new GoalData(id) { _stage = EGoalStage.Enabled, isPatched = true };
        return data;
    }

    public byte[] Export()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(GameMain.data.gameDesc.isSandboxMode);
        writer.Write(GameMain.sandboxToolsEnabled);
        writer.Write((int)GameMain.data.gameDesc.goalLevel);
        var system = GameMain.data.goalSystem;
        writer.Write(system.goalDatas.Count);
        foreach (var entry in system.goalDatas.OrderBy(x => x.Key))
        {
            var data = entry.Value;
            writer.Write(entry.Key); writer.Write((int)data.stage);
            writer.Write(data.currentValue); writer.Write(data.targetValue);
            writer.Write(data.isManualIgnore); writer.Write(data.isPatched); writer.Write(data.currentIgnoreLevel);
        }
        writer.Write(system.queueCursor);
        for (var i = 0; i < system.queueCursor; i++) writer.Write(system.goalQueue[i]);
        writer.Write((int)legacyLevel);
        writer.Write(legacyIgnored.Count);
        foreach (var id in legacyIgnored.OrderBy(x => x)) writer.Write(id);
        return stream.ToArray();
    }

    public void Apply(byte[] bytes)
    {
        var system = GameMain.data.goalSystem;
        var snapshot = GoalSnapshot.Read(bytes, LDB.goals.Length, system.goalQueue.Length);
        if (snapshot.Goals.Any(goal => !system.goalDatas.ContainsKey(goal.Id)) ||
            snapshot.Queue.Any(id => !system.goalDatas.ContainsKey(id)) ||
            snapshot.IgnoredGoals.Any(id => !system.goalDatas.ContainsKey(id)))
            throw new ParseException("Unknown goal prototype");
        Applying = true;
        try
        {
            GameMain.data.gameDesc.isSandboxMode = snapshot.SandboxMode;
            GameMain.sandboxToolsEnabled = snapshot.SandboxToolsEnabled;
            foreach (var goal in snapshot.Goals)
            {
                var data = system.goalDatas[goal.Id];
                var changed = data.stage != goal.Stage;
                data._stage = goal.Stage; data.currentValue = goal.Current; data.targetValue = goal.Target;
                data.isManualIgnore = false; data.isPatched = goal.Patched; data.currentIgnoreLevel = goal.IgnoreLevel;
                if (displayData.TryGetValue(goal.Id, out var visual)) SyncDisplayGoal(goal.Id, data, visual);
                // displayingState belongs to each UI, not to the host's renderer.
                if (changed) GameMain.gameScenario?.goalLogic?.NotifyOnGoalStageChanged(goal.Id, (int)goal.Stage);
            }
            Array.Clear(system.goalQueue, 0, system.goalQueue.Length);
            system.queueCursor = snapshot.Queue.Length;
            Array.Copy(snapshot.Queue, system.goalQueue, snapshot.Queue.Length);
            snapshotReady = true;
        }
        finally { Applying = false; }
    }

    public void Receive(GoalSnapshotPacket packet)
    {
        if (packet.Version <= version) return;
        Apply(packet.Data);
        version = packet.Version;
    }

    public void GameTick()
    {
        if (!Multiplayer.Session.IsGameLoaded || GameMain.data?.goalSystem == null) return;
        var tick = GameMain.gameTick;
        if (tick == lastTick) return;
        lastTick = tick;
        if (Multiplayer.Session.IsServer)
        {
            if (!Dirty && tick % 30 != 0) return;
            Dirty = false;
            var bytes = Export();
            if (lastSnapshot != null && bytes.SequenceEqual(lastSnapshot)) return;
            lastSnapshot = bytes;
            Multiplayer.Session.Server.SendPacket(new GoalSnapshotPacket { Version = ++version, Data = bytes });
            return;
        }
        if (!snapshotReady || !GameMain.gameScenario.goalLogic.isInit) return;
        CollectingPersonal = true;
        try
        {
            foreach (var goal in GameMain.data.goalSystem.goalDatas.Values)
            {
                var proto = LDB.goals.Select(goal.protoId);
                if (proto == null || !PersonalDeterminators.Contains(proto.DeterminatorName)) continue;
                if (goal.isIgnoredOrCompleted)
                {
                    if (collectors.TryGetValue(goal.protoId, out var old)) { old.Free(); collectors.Remove(goal.protoId); }
                    continue;
                }
                if (!collectors.TryGetValue(goal.protoId, out var collector))
                {
                    var type = typeof(GoalDeterminator).Assembly.GetType(proto.DeterminatorName);
                    collector = (GoalDeterminator)Activator.CreateInstance(type);
                    var parameters = proto.DeterminatorParams;
                    if (proto.DeterminatorName == "GD_MechaAndFactoryItemProduction")
                    {
                        if (parameters == null || parameters.Length < 3 || parameters[2] == 2) continue; // Factory-only.
                        parameters = (long[])parameters.Clone();
                        parameters[2] = 1; // Only this player's forge; factory production remains server-owned.
                        if (parameters.Length > 3) parameters[3] = 0;
                    }
                    collector.Init(GameMain.data, goal.protoId, parameters);
                    collectors.Add(goal.protoId, collector);
                }
                if (collector.isInitedAndNotFinished) collector.GameTick(tick);
                var local = collector.goalData;
                if (local == null || local.currentValue <= 0) continue;
                sentProgress.TryGetValue(goal.protoId, out var last);
                var progress = local.currentValue;
                if (progress <= last && local.stage != EGoalStage.Completed) continue;
                if (tick % 30 != 0 && local.stage != EGoalStage.Completed) continue;
                if (progress == last) continue;
                sentProgress[goal.protoId] = progress;
                Multiplayer.Session.Network.SendPacket(new GoalObservationPacket
                {
                    GoalId = goal.protoId,
                    Sequence = ++observationSequence,
                    Value = progress,
                    Target = local.targetValue,
                    Complete = local.stage == EGoalStage.Completed
                });
            }
        }
        finally { CollectingPersonal = false; }
    }

    public void Observe(ushort playerId, GoalObservationPacket packet)
    {
        var proto = LDB.goals.Select(packet.GoalId);
        if (proto == null || !PersonalDeterminators.Contains(proto.DeterminatorName) || packet.Sequence <= 0 ||
            packet.Value < 0 || packet.Target <= 0) return;
        var goal = GameMain.data.goalSystem.GetGoalDataById(packet.GoalId);
        if (goal == null || goal.isIgnoredOrCompleted) return;
        var key = (playerId, packet.GoalId);
        observed.TryGetValue(key, out var previous);
        if (packet.Sequence <= previous.Sequence || packet.Value < previous.Value) return;
        var target = goal.targetValue;
        if (target <= 0 || packet.Target != target || packet.Value > target) return;
        observed[key] = (packet.Sequence, packet.Value);
        var progress = proto.DeterminatorName == "GD_MechaAndFactoryItemProduction" ?
            goal.currentValue + packet.Value - previous.Value : Math.Max(goal.currentValue, packet.Value);
        goal.SetProgress(progress, target);
        if (goal.currentValue >= target && (packet.Complete || proto.DeterminatorName == "GD_MechaAndFactoryItemProduction"))
            goal.stage = EGoalStage.Completed;
        Dirty = true;
    }

    public void RemovePlayer(ushort id)
    {
        foreach (var key in observed.Keys.Where(x => x.Player == id).ToArray()) observed.Remove(key);
    }

    public void Dispose()
    {
        foreach (var collector in collectors.Values) collector.Free();
        collectors.Clear(); personalData.Clear(); displayData.Clear(); localIgnored.Clear();
        observed.Clear(); sentProgress.Clear();
    }
}
