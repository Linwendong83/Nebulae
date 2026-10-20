#region

using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Session;

#endregion

namespace NebulaeWorld.GameStates;

internal class GameStatesManager : IDisposable
{
    public const float MaxUPS = 240f;
    public const float MinUPS = 1f;
    private static readonly HashSet<int> preservedFeatureKeys = new();
    private static readonly HashSet<int> preservedTutorialUnlocked = new();
    private static int bufferLength;

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
    private float averageUPS = 60f;
    private int averageRTT;
    private bool hasClockSample;

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
        return averageUPS;
    }

    public void ProcessGameStateUpdatePacket(long sentTime, long gameTick, float unitsPerSecond, float simSpeed)
    {
        var rtt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sentTime;
        if (rtt < 0 || unitsPerSecond <= 0 || float.IsNaN(unitsPerSecond) || float.IsInfinity(unitsPerSecond)) return;
        // Accept the first clock observation before applying an outlier threshold.
        if (!hasClockSample)
        {
            averageRTT = (int)Math.Min(int.MaxValue, rtt);
            averageUPS = unitsPerSecond;
            hasClockSample = true;
        }
        averageRTT = (int)(averageRTT * 0.8 + rtt * 0.2);
        averageUPS = averageUPS * 0.8f + unitsPerSecond * 0.2f;

        // The host reports the world sim speed it dialed in with the outer-space UIUPSTool.
        // Adopting it keeps our Time.fixedDeltaTime in step with the host's clock and the
        // vanilla speed readout honest; the vanilla tool lerps toward its own target, so the
        // target is re-aimed as well to stop the two from fighting.
        var speed = simSpeed > 0.05f ? simSpeed : 1f;
        FPSController.SimulateSpeed = speed;
        var upsTool = UIRoot.instance?.uiGame?.upsTool;
        if (upsTool != null)
        {
            upsTool.targetSimSpeed = speed;
        }

        // We offset the tick received to account for the time it took to receive the packet
        var tickOffsetSinceSent = (long)Math.Round(unitsPerSecond * rtt / 2 / 1000);
        var currentGameTick = gameTick + tickOffsetSinceSent;
        var diff = currentGameTick - GameMain.gameTick;

        // Saving or a delayed response can produce an obsolete clock observation.
        if (rtt > 2 * averageRTT || averageUPS - unitsPerSecond > 15)
        {
            return;
        }

        // Catch up by executing native simulation steps. Changing gameTick here would skip
        // production, movement and event processing without advancing their actual state.
        var factor = speedFactor();
        var fixUps = ComputeFixUPS(diff, averageUPS, factor);
        FPSController.SetFixUPS(fixUps);

        // FPSController only folds SimulateSpeed into Time.fixedDeltaTime while the local
        // player is sailing in the running scene, so the compensation uses the same condition.
        float speedFactor()
        {
            return IsSimulateSpeedActive(speed) ? speed : 1f;
        }
    }

    private static bool IsSimulateSpeedActive(float speed)
    {
        return speed != 1f && GameMain.data != null && !DSPGame.IsMenuDemo && GameMain.isRunning && !GameMain.isLoading &&
               !GameMain.inOtherScene && GameMain.mainPlayer != null && GameMain.mainPlayer.sailing && GameMain.mainPlayer.isAlive;
    }

    /// <summary>
    ///     Adjusts the execution rate without inventing or rewinding simulation ticks.
    ///     FPSController applies speedFactor to this rate while sailing.
    /// </summary>
    internal static float ComputeFixUPS(float diff, float averageUPS, float speedFactor)
    {
        return Math.Max(MinUPS, Math.Min(MaxUPS, (diff + averageUPS) / speedFactor));
    }

    public static void UpdateBufferLength(int length)
    {
        if (length <= 0)
        {
            return;
        }
        bufferLength = length;
    }

    public static string LoadingMessage()
    {
        var progress = bufferLength * 100f / FragmentSize;
        return string.Format("Downloading {0:n0} KB ({1:F1}%)".Translate(), FragmentSize / 1000, progress);
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
        InstallWorldClock();
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
            using (var reader = new BinaryUtils.Reader(historyBinaryData))
            {
                data.history.Import(reader.BinaryReader);
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
            using (var reader = new BinaryUtils.Reader(galacticTransportBinaryData))
            {
                data.galacticTransport.Import(reader.BinaryReader);
            }
            galacticTransportBinaryData = null;
        }
        if (spaceSectorBinaryData != null)
        {
            using (Multiplayer.Session.Enemies.IsIncomingRequest.On())
            {
                data.spaceSector.isCombatMode = data.gameDesc.isCombatMode;
                using (var reader = new BinaryUtils.Reader(spaceSectorBinaryData))
                {
                    SpaceSectorBootstrap.Import(data.spaceSector, reader.BinaryReader);
                }
                data.mainPlayer.mecha.CheckCombatModuleDataIsValidPatch();
            }
            spaceSectorBinaryData = null;
        }
        if (milestoneSystemBinaryData != null)
        {
            data.milestoneSystem.Init(data);
            using (var reader = new BinaryUtils.Reader(milestoneSystemBinaryData))
            {
                data.milestoneSystem.Import(reader.BinaryReader);
            }
            milestoneSystemBinaryData = null;
        }
        if (trashSystemBinaryData != null)
        {
            using (var reader = new BinaryUtils.Reader(trashSystemBinaryData))
            {
                data.trashSystem.Import(reader.BinaryReader);
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
            using (var reader = new BinaryUtils.Reader(galacticDigitalBinaryData))
            {
                data.galacticDigital.Import(reader.BinaryReader);
            }
            galacticDigitalBinaryData = null;

        }
    }

    internal void InstallWorldClock()
    {
        if (initialSimulationTick < 0) return;
        GameMain.gameTick = initialSimulationTick;
        initialSimulationTick = -1;
    }

}
