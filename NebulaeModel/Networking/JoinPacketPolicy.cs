using System;
using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Routers;
using NebulaeModel.Packets.Session;
using NebulaeModel.Networking.Serialization;

namespace NebulaeModel.Networking;

/// <summary>Every wire type has an explicit join policy. Null means control, personal UI,
/// visual baseline, or the independently sequenced authority protocol.</summary>
internal sealed class JoinPacketPolicy
{
    public const int Ignore = int.MinValue;
    private const int Envelope = int.MaxValue;
    private readonly Dictionary<ulong, Func<object, int>> policies = new();
    private readonly NebulaeNetPacketProcessor decoder = new();
    private readonly List<JoinSyncBuffer.Change> changes = new();
    private int depth;
    private byte[] currentBytes;
    public JoinPacketPolicy()
    {
        PacketUtils.RegisterNestedTypes(decoder);
        Register<StateDeltaPacket>(null);
        Register<StateResyncPacket>(null);
        Register<NebulaeModel.Packets.Authority.AuthorityCommandPacket>(null);
        Register<NebulaeModel.Packets.Authority.AuthorityCommandResultPacket>(null);
        Register<NebulaeModel.Packets.Authority.AuthorityLifecyclePacket>(null);
        Register<NebulaeModel.Packets.Authority.AuthorityPlayerCombatStatePacket>(null);
        Register<NebulaeModel.Packets.Authority.AuthorityScopeControlPacket>(null);
        Register<NebulaeModel.Packets.Authority.AuthorityScopeDigestPacket>(null);
        Register<NebulaeModel.Packets.Authority.AuthoritySnapshotAckPacket>(null);
        Register<NebulaeModel.Packets.Authority.AuthoritySnapshotBeginPacket>(null);
        Register<NebulaeModel.Packets.Authority.AuthoritySnapshotChunkPacket>(null);
        Register<NebulaeModel.Packets.Authority.AuthoritySnapshotCommitPacket>(null);
        Register<NebulaeModel.Packets.Authority.AuthorityWelcomePacket>(null);
        Register<NebulaeModel.Packets.Authority.AuthorityWorldStatePacket>(null);
        Register<NebulaeModel.Packets.Authority.GroundBaseState>(null);
        Register<NebulaeModel.Packets.Chat.NewChatMessagePacket>(null);
        Register<NebulaeModel.Packets.Combat.BattleVisualPacket>(null);
        Register<NebulaeModel.Packets.Combat.CombatAggressivenessUpdatePacket>(_ => 0);
        Register<NebulaeModel.Packets.Combat.CombatGenerationPacket>(null);
        Register<NebulaeModel.Packets.Combat.CombatTruceUpdatePacket>(_ => 0);
        Register<NebulaeModel.Packets.Combat.Mecha.PlayerLifePacket>(null);
        Register<NebulaeModel.Packets.Combat.PlayerWeaponPacket>(null);
        Register<NebulaeModel.Packets.Factory.Assembler.FactoryProductPacket>(p => p.Key?.Length == AuthorityLimits.ObjectKeyBytes ? ObjectKeyCodec.ReadRaw(p.Key, 0).Scope : Ignore);
        Register<NebulaeModel.Packets.Factory.BattleBase.BattleBaseSettingUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Belt.BeltReverseRequestPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Belt.BeltSignalIconPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Belt.BeltSignalNumberPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Belt.BeltUpdatePickupItemsPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Belt.BeltUpdatePutItemOnPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Belt.BeltUpdatePutItemsPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Belt.ConnectToMonitorPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Belt.ConnectToSpraycoaterPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.BuildDroneLaunchPacket>(null);
        Register<NebulaeModel.Packets.Factory.BuildEntityRequest>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.BuildTargetAssignmentPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.BuildTargetAssignmentReplyPacket>(null);
        Register<NebulaeModel.Packets.Factory.BuildTargetBaseReleaseAckPacket>(null);
        Register<NebulaeModel.Packets.Factory.BuildTargetReadyPacket>(null);
        Register<NebulaeModel.Packets.Factory.CreatePrebuildsRequest>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.DestructEntityRequest>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Ejector.EjectorAutoOrbitUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Ejector.EjectorOrbitUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.EntityBoostSwitchPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.EntityWarningSwitchPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.ExtraInfoUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Foundation.FoundationBlueprintPastePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Foundation.FoundationBuildUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Foundation.PlanetReformPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Foundation.VeinPosUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Inserter.InserterFilterUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Inserter.InserterItemUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Inserter.InserterOffsetCorrectionPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Inserter.NewSetInserterInsertTargetPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Inserter.NewSetInserterPickTargetPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Miner.MinerStoragePickupPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Monitor.MonitorSettingUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Monitor.SpeakerSettingUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.PasteBuildingSettingUpdate>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.PowerExchanger.PowerExchangerChangeModePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.PowerExchanger.PowerExchangerStorageUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.PowerGenerator.PowerGeneratorFuelUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.PowerGenerator.PowerGeneratorProductUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.PowerTower.PowerTowerChargerUpdate>(null);
        Register<NebulaeModel.Packets.Factory.PrebuildItemRequiredUpdate>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.PrebuildReconstructPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.RayReceiver.RayReceiverChangeLensPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.RayReceiver.RayReceiverChangeModePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Splitter.SplitterFilterChangePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Splitter.SplitterPriorityChangePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Storage.StorageSyncRealtimeChangePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Storage.StorageSyncRequestPacket>(null);
        Register<NebulaeModel.Packets.Factory.Storage.StorageSyncResponsePacket>(null);
        Register<NebulaeModel.Packets.Factory.Storage.StorageSyncSetBansPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Storage.StorageSyncSetFilterPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Storage.StorageSyncSortPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Tank.SprayerStorageUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Tank.TankInputOutputSwitchPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Tank.TankStorageUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.UpgradeEntityRequest>(p => p.PlanetId);
        Register<NebulaeModel.Packets.GameHistory.GameHistoryEnqueueTechPacket>(_ => 0);
        Register<NebulaeModel.Packets.GameHistory.GameHistoryFeatureKeyPacket>(null);
        Register<NebulaeModel.Packets.GameHistory.GameHistoryNotificationPacket>(p => p.Event is
            NebulaeModel.Packets.GameHistory.GameHistoryEvent.PauseQueue or NebulaeModel.Packets.GameHistory.GameHistoryEvent.ResumeQueue ? 0 : Ignore);
        Register<NebulaeModel.Packets.GameHistory.GameHistoryRemoveTechPacket>(_ => 0);
        Register<NebulaeModel.Packets.GameHistory.GameHistoryResearchUpdatePacket>(_ => 0);
        Register<NebulaeModel.Packets.GameHistory.GameHistoryTechQueueSyncPacket>(_ => 0);
        Register<NebulaeModel.Packets.GameHistory.GameHistoryUnlockTechPacket>(_ => 0);
        Register<NebulaeModel.Packets.GameStates.FragmentInfo>(null);
        Register<NebulaeModel.Packets.GameStates.GamePausePacket>(_ => 0);
        Register<NebulaeModel.Packets.GameStates.GameStateRequest>(null);
        Register<NebulaeModel.Packets.GameStates.GameStateSaveInfoPacket>(_ => 0);
        Register<NebulaeModel.Packets.GameStates.GameStateUpdate>(null);
        Register<NebulaeModel.Packets.GameStates.GoalObservationPacket>(null);
        Register<NebulaeModel.Packets.GameStates.GoalSnapshotPacket>(_ => 0);
        Register<NebulaeModel.Packets.GameStates.MetadataOperationPacket>(null);
        Register<NebulaeModel.Packets.GameStates.MetadataProductionPacket>(null);
        Register<NebulaeModel.Packets.GameStates.MetadataReceiptPacket>(null);
        Register<NebulaeModel.Packets.GameStates.PropertyHistoryPacket>(null);
        Register<NebulaeModel.Packets.GameStates.SimSpeedPacket>(_ => 0);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.FacilityDirectoryPacket>(null);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.FacilityDirectoryRequest>(null);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.FacilityRowsSubscription>(null);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.LCPAdvancedMinerEntryUpdate>(null);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.LCPDispenserEntryUpdate>(null);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.LCPFilterResultsRequest>(null);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.LCPFilterResultsResponse>(null);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.LCPObjectEntryEntityInfo>(null);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.LCPObjectEntryRequest>(null);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.LCPStationEntryUpdate>(null);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.LCPStationNameSearchPacket>(null);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.LCPStationRouteResultsPacket>(null);
        Register<NebulaeModel.Packets.Logistics.DispenserAddTakePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Logistics.DispenserCourierPacket>(null);
        Register<NebulaeModel.Packets.Logistics.DispenserSettingPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Logistics.DispenserStorePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Logistics.ILSAddStationComponent>(p => -p.PlanetId);
        Register<NebulaeModel.Packets.Logistics.ILSArriveStarPlanetRequest>(null);
        Register<NebulaeModel.Packets.Logistics.ILSArriveStarPlanetResponse>(null);
        Register<NebulaeModel.Packets.Logistics.ILSgStationPoolSync>(null);
        Register<NebulaeModel.Packets.Logistics.ILSRemoveStationComponent>(p => -p.PlanetId);
        Register<NebulaeModel.Packets.Logistics.ILSRequestgStationPoolSync>(null);
        Register<NebulaeModel.Packets.Logistics.ILSShipLifecyclePacket>(p => -p.PlanetId);
        Register<NebulaeModel.Packets.Logistics.ILSStorageCommitPacket>(p => -p.PlanetId);
        Register<NebulaeModel.Packets.Logistics.ILSUpdateRoute>(_ => 0);
        Register<NebulaeModel.Packets.Logistics.ILSUpdateSlotData>(p => -p.PlanetId);
        Register<NebulaeModel.Packets.Logistics.RemoteOrderUpdate>(_ => 0);
        Register<NebulaeModel.Packets.Logistics.StationStorageUIBatchPacket>(p => -p.PlanetId);
        Register<NebulaeModel.Packets.Logistics.StationUI>(p => -p.PlanetId);
        Register<NebulaeModel.Packets.Logistics.StationUIInitialSync>(null);
        Register<NebulaeModel.Packets.Logistics.StationUIInitialSyncRequest>(null);
        Register<NebulaeModel.Packets.Logistics.StorageUI>(p => -p.PlanetId);
        Register<NebulaeModel.Packets.Planet.FactoryData>(null);
        Register<NebulaeModel.Packets.Planet.FactoryLoadRequest>(null);
        Register<NebulaeModel.Packets.Planet.PlanetDataRequest>(null);
        Register<NebulaeModel.Packets.Planet.PlanetDataResponse>(null);
        Register<NebulaeModel.Packets.Planet.PlanetDetailRequest>(null);
        Register<NebulaeModel.Packets.Planet.PlanetDetailResponse>(null);
        Register<NebulaeModel.Packets.Planet.VegeAddPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Planet.VegeMinedPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Players.PlayerDashboardPacket>(null);
        Register<NebulaeModel.Packets.Players.PlayerEjectMechaDronePacket>(null);
        Register<NebulaeModel.Packets.Players.PlayerGiveItemPacket>(null);
        Register<NebulaeModel.Packets.Players.PlayerLatencyPacket>(null);
        Register<NebulaeModel.Packets.Players.PlayerMapPosition>(null);
        Register<NebulaeModel.Packets.Players.PlayerMechaArmor>(null);
        Register<NebulaeModel.Packets.Players.PlayerMechaDIYArmor>(null);
        Register<NebulaeModel.Packets.Players.PlayerMechaStat>(null);
        Register<NebulaeModel.Packets.Players.PlayerMovement>(null);
        Register<NebulaeModel.Packets.Players.PlayerMovementDisplay>(null);
        Register<NebulaeModel.Packets.Players.PlayerSandCount>(null);
        Register<NebulaeModel.Packets.Players.PlayerSandGained>(null);
        Register<NebulaeModel.Packets.Players.PlayerUpdateLocalStarId>(null);
        Register<NebulaeModel.Packets.Players.VegetableCollectionSnapshotPacket>(null);
        Register<NebulaeModel.Packets.Routers.PlanetBroadcastPacket>(_ => Envelope);
        Register<NebulaeModel.Packets.Routers.StarBroadcastPacket>(_ => Envelope);
        Register<NebulaeModel.Packets.Session.GlobalGameDataRequest>(null);
        Register<NebulaeModel.Packets.Session.GlobalGameDataResponse>(p => p.DataType == GlobalGameDataResponse.EDataType.PlanetBaseline ? 0 : Ignore);
        Register<NebulaeModel.Packets.Session.HandshakeResponse>(null);
        Register<NebulaeModel.Packets.Session.JoinSyncAck>(null);
        Register<NebulaeModel.Packets.Session.JoinSyncBarrier>(null);
        Register<NebulaeModel.Packets.Session.JoinSyncBatch>(null);
        Register<NebulaeModel.Packets.Session.JoinSyncBegin>(null);
        Register<NebulaeModel.Packets.Session.JoinSyncProgress>(null);
        Register<NebulaeModel.Packets.Session.JoinDataTransferBegin>(null);
        Register<NebulaeModel.Packets.Session.JoinDataTransferChunk>(null);
        Register<NebulaeModel.Packets.Session.LobbyRequest>(null);
        Register<NebulaeModel.Packets.Session.LobbyResponse>(null);
        Register<NebulaeModel.Packets.Session.LobbyUpdateCombatValues>(null);
        Register<NebulaeModel.Packets.Session.LobbyUpdateValues>(null);
        Register<NebulaeModel.Packets.Session.PlayerDisconnected>(_ => 0);
        Register<NebulaeModel.Packets.Session.PlayerJoining>(_ => 0);
        Register<NebulaeModel.Packets.Session.ServerStatusRequest>(null);
        Register<NebulaeModel.Packets.Session.ServerStatusResponse>(null);
        Register<NebulaeModel.Packets.Session.StartGameMessage>(null);
        Register<NebulaeModel.Packets.Session.SyncComplete>(null);
        Register<NebulaeModel.Packets.Statistics.KillStatisticsPacket>(null);
        Register<NebulaeModel.Packets.Statistics.KillStatisticsRequest>(null);
        Register<NebulaeModel.Packets.Statistics.MilestoneUnlockPacket>(_ => 0);
        Register<NebulaeModel.Packets.Statistics.PlayerMechaStatBatchPacket>(null);
        Register<NebulaeModel.Packets.Statistics.StatisticsDataPacket>(null);
        Register<NebulaeModel.Packets.Statistics.StatisticsExtraDataPacket>(null);
        Register<NebulaeModel.Packets.Statistics.StatisticsReferenceSpeedTipPacket>(null);
        Register<NebulaeModel.Packets.Statistics.StatisticsRequestEvent>(null);
        Register<NebulaeModel.Packets.Statistics.StatisticUpdateDataPacket>(null);
        Register<NebulaeModel.Packets.Trash.PersistentDropPacket>(_ => 0);
        Register<NebulaeModel.Packets.Trash.TrashSystemClearAllTrashPacket>(_ => 0);
        Register<NebulaeModel.Packets.Trash.TrashSystemLootFilterPacket>(_ => 0);
        Register<NebulaeModel.Packets.Trash.TrashSystemNewPlanetTrashPacket>(_ => 0);
        Register<NebulaeModel.Packets.Trash.TrashSystemNewPlayerTrashPacket>(_ => 0);
        Register<NebulaeModel.Packets.Trash.TrashSystemTrashRemovedPacket>(_ => 0);
        Register<NebulaeModel.Packets.Universe.DysonLaunchDataPacket>(null);
        Register<NebulaeModel.Packets.Universe.DysonSailBatchPacket>(null);
        Register<NebulaeModel.Packets.Universe.DysonSphereData>(null);
        Register<NebulaeModel.Packets.Universe.DysonSphereLoadRequest>(null);
        Register<NebulaeModel.Packets.Universe.HivePreviewRequest>(null);
        Register<NebulaeModel.Packets.Universe.DysonSphereStatusPacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonBlueprintPacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonSphereAddFramePacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonSphereAddLayerPacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonSphereAddNodePacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonSphereAddShellPacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonSphereColorChangeBatchPacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonSphereEditLayerPacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonSpherePaintCellsPacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonSphereRemoveFramePacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonSphereRemoveLayerPacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonSphereRemoveNodePacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonSphereRemoveShellPacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonSwarmAddOrbitPacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonSwarmEditOrbitPacket>(null);
        Register<NebulaeModel.Packets.Universe.Editor.DysonSwarmRemoveOrbitPacket>(null);
        Register<NebulaeModel.Packets.Universe.MarkerSettingUpdatePacket>(_ => 0);
        Register<NebulaeModel.Packets.Universe.NameInputPacket>(null);
        Register<NebulaeModel.Packets.Universe.PlanetMemoUpdatePacket>(_ => 0);
        Register<NebulaeModel.Packets.Warning.WarningBroadcastDataPacket>(null);
        Register<NebulaeModel.Packets.Warning.WarningDataPacket>(null);
        Register<NebulaeModel.Packets.Warning.WarningDataRequest>(null);
        Register<NebulaeModel.Packets.Warning.WarningSignalPacket>(null);
    }

    public bool Contains(Type type) => policies.ContainsKey(WireContract.PacketId(type));

    private void Register<T>(Func<T, int> policy) where T : class, new()
    {
        policies.Add(WireContract.PacketId(typeof(T)), policy == null ? null : p => policy((T)p));
        if (policy != null)
            decoder.Subscribe<T>(packet => Describe(packet, currentBytes), () => new T());
    }

    public JoinSyncBuffer.Change[] Describe<T>(T packet, byte[] bytes) where T : class
    {
        lock (policies)
        {
            if (depth == 0) changes.Clear();
            if (++depth > 8) { depth--; throw new InvalidOperationException("Nested join envelope is too deep."); }
            try
            {
                if (!policies.TryGetValue(WireContract.PacketId(packet.GetType()), out var policy))
                    throw new InvalidOperationException("Missing join policy for " + packet.GetType());
                var scope = policy?.Invoke(packet) ?? Ignore;
                if (scope == Envelope)
                {
                    var inner = packet is StarBroadcastPacket star ? star.PacketObjects : ((PlanetBroadcastPacket)(object)packet).PacketObjects;
                    if (inner == null || inner.Length > JoinSyncBuffer.BatchCount) throw new InvalidOperationException("Invalid join envelope.");
                    foreach (var raw in inner) Decode(raw);
                }
                else if (scope != Ignore) changes.Add(new JoinSyncBuffer.Change(scope, bytes));
                return depth == 1 ? changes.ToArray() : Array.Empty<JoinSyncBuffer.Change>();
            }
            finally { depth--; }
        }
    }

    private void Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 8) throw new InvalidOperationException("Truncated join packet.");
        var id = BitConverter.ToUInt64(bytes, 0);
        if (!policies.TryGetValue(id, out var policy)) throw new InvalidOperationException("Unknown join packet.");
        if (policy == null) return;
        var previous = currentBytes;
        try { currentBytes = bytes; decoder.ReadPacket(new NetDataReader(bytes)); }
        finally { currentBytes = previous; }
    }

    public bool CanReplay(byte[] bytes, int scope)
    {
        lock (policies)
        {
            changes.Clear();
            // Keep nested decoding from clearing the collector.
            depth = 1;
            try { Decode(bytes); return changes.Count == 1 && changes[0].Scope == scope; }
            finally { depth = 0; }
        }
    }
}
