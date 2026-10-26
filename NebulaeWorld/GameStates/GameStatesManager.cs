#region

using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.Networking;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Session;

#endregion

namespace NebulaeWorld.GameStates;

internal class GameStatesManager : IDisposable
{
    public const float MaxUPS = 240f;
    public const float MinUPS = 1f;
    private static readonly HashSet<int> preservedFeatureKeys = new();
    private static readonly HashSet<int> preservedTutorialUnlocked = new();

    public static bool IsAdvisorOrTutorialFeatureKey(int featureId)
    {
        return (featureId >= FeatureID.ADVISOR_TIP_START && featureId < FeatureID.GOAL_STATE) ||
               (featureId >= FeatureID.VEIN_SCAN && featureId <= FeatureID.HIDE_GRID_SPLIT_TIP);
    }

    public static void PreserveFeatureKey(int featureId)
    {
        if (IsAdvisorOrTutorialFeatureKey(featureId))
        {
            if (preservedFeatureKeys.Add(featureId)) SavePersonalHistory();
        }
    }

    public static void UnpreserveFeatureKey(int featureId)
    {
        if (IsAdvisorOrTutorialFeatureKey(featureId))
        {
            if (preservedFeatureKeys.Remove(featureId)) SavePersonalHistory();
        }
    }

    public static void PreserveTutorial(int tutorialId)
    {
        if (tutorialId > 0)
        {
            if (preservedTutorialUnlocked.Add(tutorialId)) SavePersonalHistory();
        }
    }

    public static void SavePersonalHistory() => LocalPlayerPreferences.SaveTutorials(
        preservedFeatureKeys.ToArray(), preservedTutorialUnlocked.ToArray());

    public static void RestorePersonalHistory(GameHistoryData history)
    {
        LocalPlayerPreferences.LoadTutorials(out var keys, out var tutorials);
        foreach (var key in keys) if (IsAdvisorOrTutorialFeatureKey(key)) preservedFeatureKeys.Add(key);
        foreach (var tutorial in tutorials) if (tutorial > 0) preservedTutorialUnlocked.Add(tutorial);
        history.featureKeys.RemoveWhere(IsAdvisorOrTutorialFeatureKey);
        history.featureKeys.UnionWith(preservedFeatureKeys);
        history.tutorialUnlocked.Clear();
        history.tutorialUnlocked.UnionWith(preservedTutorialUnlocked);
    }

    public static long RealGameTick => GameMain.gameTick;
    public static float RealUPS => (float)FPSController.currentUPS;
    public static long LastSaveTime { get; set; } // UnixTimeSeconds
    public static int FragmentSize { get; set; }

    // UPS syncing by GameStateUpdate packet
    private float serverUPS = 60f;
    private bool hasClockSample;
    private long lastClockTick;
    private long lastClockRequestTime;
    private double lastClockCorrectionTime;
    private bool clockCorrectionActive;
    private float lastClockSpeed = 1f;
    internal const double ClockCorrectionLeaseSeconds = 3;
    internal Func<double> ClockTime { get; set; } = () => Diagnostics.Now;
    private double ClockNow => ClockTime?.Invoke() ?? Diagnostics.Now;

    // Store data get from GlobalGameDataResponse
    private bool sandboxToolsEnabled;
    private byte[] historyBinaryData;
    private byte[] galacticTransportBinaryData;
    private byte[] spaceSectorBinaryData;
    private byte[] milestoneSystemBinaryData;
    private byte[] trashSystemBinaryData;
    private byte[] galacticDigitalBinaryData;
    private byte[] goalBinaryData;
    private byte[] killBinaryData;
    private int birthStarId;
    private int birthPlanetId;
    private EGoalLevel goalLevel;
    private long initialSimulationTick = -1;

    public void ApplyBirthData(GameData data)
    {
        if (birthPlanetId <= 0 || data?.galaxy == null) return;
        data.galaxy.birthStarId = birthStarId;
        data.galaxy.birthPlanetId = birthPlanetId;
        data.gameDesc.goalLevel = Multiplayer.Session.IsClient ? Multiplayer.Session.Goals.PersonalLevel : goalLevel;
    }

    public GameStatesManager()
    {
        LastSaveTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public void Dispose()
    {
        hasClockSample = clockCorrectionActive = false;
        preservedFeatureKeys.Clear();
        preservedTutorialUnlocked.Clear();

        LastSaveTime = FragmentSize = 0;
        sandboxToolsEnabled = false;
        historyBinaryData = null;
        galacticTransportBinaryData = null;
        spaceSectorBinaryData = null;
        milestoneSystemBinaryData = null;
        trashSystemBinaryData = null;
        galacticDigitalBinaryData = null;
        GC.SuppressFinalize(this);
    }

    public float GetServerUPS()
    {
        return serverUPS;
    }

    public void ProcessGameStateUpdatePacket(long sentTime, long gameTick, float unitsPerSecond, float simSpeed,
        double queuedSeconds = 0)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (sentTime <= 0 || sentTime > now || gameTick < 0 || unitsPerSecond <= 0 ||
            float.IsNaN(unitsPerSecond) || float.IsInfinity(unitsPerSecond) ||
            float.IsNaN(simSpeed) || float.IsInfinity(simSpeed) || simSpeed < 0.1f || simSpeed > 8f) return;
        // A delayed duplicate cannot replace a newer host observation or renew its correction.
        if (hasClockSample && (gameTick < lastClockTick ||
            gameTick == lastClockTick && sentTime <= lastClockRequestTime)) return;
        var rttSeconds = (now - sentTime) / 1000d;
        hasClockSample = true;
        lastClockTick = gameTick;
        lastClockRequestTime = sentTime;
        // A real host slowdown must take effect now. Rejecting it as a rate outlier lets the
        // client run ahead for several seconds and then crawl while the host catches up.
        serverUPS = unitsPerSecond;
        lastClockSpeed = simSpeed;

        // The host reports the world sim speed it dialed in with the outer-space UIUPSTool.
        // Adopting it keeps our Time.fixedDeltaTime in step with the host's clock and the
        // vanilla speed readout honest; the vanilla tool lerps toward its own target, so the
        // target is re-aimed as well to stop the two from fighting.
        ApplyWorldSimSpeed(simSpeed);
        var factor = IsSimulateSpeedActive(simSpeed) ? simSpeed : 1f;
        var diff = 0d;
        if (rttSeconds <= ClockCorrectionLeaseSeconds)
        {
            // The host sampled after our request and before this reply reached the receive
            // queue. Only the local queue age is known; RTT/2 is not a measured one-way delay.
            // Adjust phase only outside that interval, with one tick for integer quantization.
            if (double.IsNaN(queuedSeconds) || double.IsInfinity(queuedSeconds)) queuedSeconds = 0;
            queuedSeconds = Math.Max(0, Math.Min(rttSeconds, queuedSeconds));
            var earliest = gameTick + unitsPerSecond * queuedSeconds;
            var latest = gameTick + unitsPerSecond * rttSeconds;
            var local = (double)GameMain.gameTick;
            if (local < earliest - 1) diff = earliest - 1 - local;
            else if (local > latest + 1) diff = latest + 1 - local;
        }
        // Old replies can still report the host's rate, but cannot justify a large phase
        // correction. Advance through native steps; never skip or rewind the simulation clock.
        var fixUps = ComputeFixUPS((float)diff, serverUPS, factor);
        clockCorrectionActive = fixUps != ComputeFixUPS(0, serverUPS, factor);
        lastClockCorrectionTime = ClockNow;
        FPSController.SetFixUPS(fixUps);
    }

    /// <summary>Expire a phase correction on the render/network loop, independently of local UPS.</summary>
    public void ServiceClock()
    {
        if (!hasClockSample || !clockCorrectionActive || Multiplayer.Session?.WorldPaused == true ||
            ClockNow - lastClockCorrectionTime < ClockCorrectionLeaseSeconds) return;
        clockCorrectionActive = false;
        var speed = Multiplayer.Session?.WorldSimSpeed ?? lastClockSpeed;
        if (speed < 0.1f || speed > 8f || float.IsNaN(speed)) speed = lastClockSpeed;
        var factor = IsSimulateSpeedActive(speed) ? speed : 1f;
        FPSController.SetFixUPS(ComputeFixUPS(0, serverUPS, factor));
        Diagnostics.Warn("clock:correction-expired", () => $"[clock] phase correction expired; following last host rate={serverUPS:F2} UPS until a new sample arrives");
    }

    private static bool IsSimulateSpeedActive(float speed)
    {
        return speed != 1f && GameMain.data != null && !DSPGame.IsMenuDemo && GameMain.isRunning && !GameMain.isLoading &&
               !GameMain.inOtherScene;
    }

    public static void ApplyWorldSimSpeed(float speed)
    {
        if (float.IsNaN(speed) || float.IsInfinity(speed) || speed < 0.1f || speed > 8f) return;
        if (Multiplayer.Session != null) Multiplayer.Session.WorldSimSpeed = speed;
        FPSController.SimulateSpeed = speed;
        var tool = UIRoot.instance?.uiGame?.upsTool;
        if (tool != null) tool.targetSimSpeed = speed;
    }

    public static void ChangeWorldSimSpeed(float speed)
    {
        if (float.IsNaN(speed) || float.IsInfinity(speed)) return;
        speed = Math.Min(8f, Math.Max(0.1f, speed));
        var session = Multiplayer.Session;
        if (session.IsClient) session.Network.SendPacket(new NebulaeModel.Packets.GameStates.SimSpeedPacket(speed));
        else
        {
            ApplyWorldSimSpeed(speed);
            session.Server?.SendPacket(new NebulaeModel.Packets.GameStates.SimSpeedPacket(speed));
        }
    }

    /// <summary>
    ///     Adjusts the execution rate without inventing or rewinding simulation ticks.
    ///     FPSController applies speedFactor to this rate while sailing.
    /// </summary>
    internal static float ComputeFixUPS(float diff, float hostUPS, float speedFactor)
    {
        return Math.Max(MinUPS, Math.Min(MaxUPS, (diff + hostUPS) / speedFactor));
    }

    public void ImportGlobalGameData(GlobalGameDataResponse packet)
    {
        switch (packet.DataType)
        {
            case GlobalGameDataResponse.EDataType.KillStatistics:
                killBinaryData = packet.BinaryData;
                break;
            case GlobalGameDataResponse.EDataType.Goals:
                goalBinaryData = packet.BinaryData;
                Multiplayer.Session.Goals.ReadDefaults(goalBinaryData);
                break;
            case GlobalGameDataResponse.EDataType.Session:
                using (var reader = new BinaryUtils.Reader(packet.BinaryData))
                {
                    var br = reader.BinaryReader;
                    SaveManager.SetWorldId(br.ReadString());
                    birthStarId = br.ReadInt32();
                    birthPlanetId = br.ReadInt32();
                    goalLevel = (EGoalLevel)br.ReadInt32();
                    Multiplayer.Session.Metadata.SourceClusterKey = br.ReadInt64();
                    initialSimulationTick = br.ReadInt64();
                }
                break;
            case GlobalGameDataResponse.EDataType.History:
                historyBinaryData = packet.BinaryData;
                break;

            case GlobalGameDataResponse.EDataType.GalacticTransport:
                galacticTransportBinaryData = packet.BinaryData;
                break;

            case GlobalGameDataResponse.EDataType.SpaceSector:
                spaceSectorBinaryData = packet.BinaryData;
                break;

            case GlobalGameDataResponse.EDataType.MilestoneSystem:
                milestoneSystemBinaryData = packet.BinaryData;
                break;

            case GlobalGameDataResponse.EDataType.TrashSystem:
                trashSystemBinaryData = packet.BinaryData;
                break;

            case GlobalGameDataResponse.EDataType.GalacticDigital:
                galacticDigitalBinaryData = packet.BinaryData;
                break;

            case GlobalGameDataResponse.EDataType.Ready:
                if (birthPlanetId <= 0) throw new System.IO.InvalidDataException("Missing session initialization");
                using (var reader = new BinaryUtils.Reader(packet.BinaryData))
                {
                    var br = reader.BinaryReader;
                    sandboxToolsEnabled = br.ReadBoolean();
                }
                if (Multiplayer.Session.Goals.PrepareClientProfile(StartClientWorld)) StartClientWorld();
                break;
        }
    }

    private static void StartClientWorld()
    {
        DSPGame.GameDesc.goalLevel = Multiplayer.Session.Goals.PersonalLevel;
        DSPGame.StartGameSkipPrologue(DSPGame.GameDesc);
    }

    public void OverwriteGlobalGameData(GameData data)
    {
        if (data == null)
        {
            return;
        }
        ApplyBirthData(data);
        InstallWorldClock(data);
        if (killBinaryData != null)
        {
            Multiplayer.Session.Kills.ApplySnapshot(killBinaryData);
            killBinaryData = null;
        }
        if (goalBinaryData != null)
        {
            Multiplayer.Session.Goals.Apply(goalBinaryData);
            goalBinaryData = null;
        }

        if (historyBinaryData != null)
        {
            GameMain.sandboxToolsEnabled = sandboxToolsEnabled;

            if (data.history != null)
            {
                if (data.history.featureKeys != null)
                {
                    foreach (int key in data.history.featureKeys)
                    {
                        PreserveFeatureKey(key);
                    }
                }
                if (data.history.tutorialUnlocked != null)
                {
                    foreach (int tutorialId in data.history.tutorialUnlocked)
                    {
                        PreserveTutorial(tutorialId);
                    }
                }
            }

            if (data.history == null)
            {
                data.history = new GameHistoryData();
            }

            data.history.Init(data);
            using (var reader = new BinaryUtils.Reader(historyBinaryData, 20))
            {
                data.history.Import(reader.BinaryReader);
                reader.RequireEnd();
            }
            historyBinaryData = null;

            data.history.featureKeys ??= new HashSet<int>();
            data.history.tutorialUnlocked ??= new HashSet<int>();
            RestorePersonalHistory(data.history);

            if (preservedFeatureKeys.Count > 0)
            {
                var keysToRestore = new List<int>(preservedFeatureKeys);
                foreach (int key in keysToRestore)
                {
                    if (!data.history.HasFeatureKey(key))
                    {
                        data.history.RegFeatureKey(key);
                    }
                }
            }
            if (preservedTutorialUnlocked.Count > 0)
            {
                var tutorialsToRestore = new List<int>(preservedTutorialUnlocked);
                foreach (int tutorialId in tutorialsToRestore)
                {
                    if (tutorialId > 0 && !data.history.TutorialUnlocked(tutorialId))
                    {
                        data.history.UnlockTutorial(tutorialId);
                    }
                }
            }

            if (data.history.featureKeys != null)
            {
                foreach (int key in data.history.featureKeys)
                {
                    PreserveFeatureKey(key);
                }
            }
            if (data.history.tutorialUnlocked != null)
            {
                foreach (int tutorialId in data.history.tutorialUnlocked)
                {
                    PreserveTutorial(tutorialId);
                }
            }

            using (Multiplayer.Session.History.IsIncomingRequest.On())
            {
                if (data.history.featureKeys != null)
                {
                    int maxAdvisorUsedKey = FeatureID.ADVISOR_TIP_USED_START + (FeatureID.ADVISOR_TIP_USED_START - FeatureID.ADVISOR_TIP_START);
                    foreach (int key in data.history.featureKeys)
                    {
                        if (key >= FeatureID.ADVISOR_TIP_START && key < FeatureID.ADVISOR_TIP_USED_START)
                        {
                            int tipId = key - FeatureID.ADVISOR_TIP_START;
                            GameMain.gameScenario?.advisorLogic?.SetAdvisorTipFinished(tipId);
                        }
                        else if (key >= FeatureID.ADVISOR_TIP_USED_START && key < maxAdvisorUsedKey)
                        {
                            int tipId = key - FeatureID.ADVISOR_TIP_USED_START;
                            GameMain.gameScenario?.advisorLogic?.SetAdvisorTipUsed(tipId);
                        }
                    }
                }
            }

            var advisorTip = UIRoot.instance?.uiGame?.advisorTip;
            if (advisorTip != null)
            {
                if (advisorTip.playingTip != null &&
                    (data.history.HasFeatureKey(FeatureID.ADVISOR_TIP_START + advisorTip.playingTip.ID) ||
                     data.history.HasFeatureKey(FeatureID.ADVISOR_TIP_USED_START + advisorTip.playingTip.ID)))
                {
                    advisorTip.StopAdvisorTip();
                }
                advisorTip.requests?.RemoveAll(id =>
                    data.history.HasFeatureKey(FeatureID.ADVISOR_TIP_START + id) ||
                    data.history.HasFeatureKey(FeatureID.ADVISOR_TIP_USED_START + id));
                if (advisorTip.nextTip != null &&
                    (data.history.HasFeatureKey(FeatureID.ADVISOR_TIP_START + advisorTip.nextTip.ID) ||
                     data.history.HasFeatureKey(FeatureID.ADVISOR_TIP_USED_START + advisorTip.nextTip.ID)))
                {
                    advisorTip.nextTip = null;
                }
            }

            var tutorialTip = UIRoot.instance?.uiGame?.tutorialTip;
            if (tutorialTip != null && tutorialTip.entryShowed != null)
            {
                for (int i = tutorialTip.entryShowed.Count - 1; i >= 0; i--)
                {
                    var entry = tutorialTip.entryShowed[i];
                    if (entry != null && data.history.TutorialUnlocked(entry.tutorialId))
                    {
                        tutorialTip.CloseTip(entry.tutorialId);
                    }
                }
            }
        }
        if (galacticTransportBinaryData != null)
        {
            data.galacticTransport.Init(data);
            using (var reader = new BinaryUtils.Reader(galacticTransportBinaryData, 1))
            {
                data.galacticTransport.Import(reader.BinaryReader);
                reader.RequireEnd();
            }
            galacticTransportBinaryData = null;
        }
        if (spaceSectorBinaryData != null)
        {
            using (Multiplayer.Session.Enemies.IsIncomingRequest.On())
            {
                data.spaceSector.isCombatMode = data.gameDesc.isCombatMode;
                using (var reader = new BinaryUtils.Reader(spaceSectorBinaryData, 1))
                {
                    SpaceSectorBootstrap.Import(data.spaceSector, reader.BinaryReader);
                    reader.RequireEnd();
                }
                data.mainPlayer.mecha.CheckCombatModuleDataIsValidPatch();
            }
            spaceSectorBinaryData = null;
        }
        if (milestoneSystemBinaryData != null)
        {
            data.milestoneSystem.Init(data);
            using (var reader = new BinaryUtils.Reader(milestoneSystemBinaryData, 0))
            {
                data.milestoneSystem.Import(reader.BinaryReader);
                reader.RequireEnd();
            }
            milestoneSystemBinaryData = null;
        }
        if (trashSystemBinaryData != null)
        {
            using (var reader = new BinaryUtils.Reader(trashSystemBinaryData, 0))
            {
                data.trashSystem.Import(reader.BinaryReader);
                reader.RequireEnd();
            }
            // Wait until WarningDataPacket to assign warningId
            var container = data.trashSystem.container;
            for (var i = 0; i < container.trashCursor; i++)
            {
                container.trashDataPool[i].warningId = -1;
            }
            trashSystemBinaryData = null;
        }
        if (galacticDigitalBinaryData != null)
        {
            using (var reader = new BinaryUtils.Reader(galacticDigitalBinaryData, 1))
            {
                data.galacticDigital.Import(reader.BinaryReader);
                reader.RequireEnd();
            }
            galacticDigitalBinaryData = null;

        }
    }

    internal void InstallWorldClock(GameData data = null)
    {
        if (initialSimulationTick < 0) return;
        hasClockSample = clockCorrectionActive = false;
        GameMain.gameTick = initialSimulationTick;
        initialSimulationTick = -1;
        // New clients generate orbital poses at t=0. Pose and clock must agree before
        // choosing a planet or restoring an inertial (sailing) player position.
        data?.galaxy?.UpdatePoses(GameMain.gameTime);
    }

}
