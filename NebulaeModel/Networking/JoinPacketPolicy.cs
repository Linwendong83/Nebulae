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
    private readonly Dictionary<ulong, Func<object, string>> coalescers = new();
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
        Register<NebulaeModel.Packets.Factory.Assembler.FactoryProductPacket>(
            p => p.Key?.Length == AuthorityLimits.ObjectKeyBytes ? ObjectKeyCodec.ReadRaw(p.Key, 0).Scope : Ignore,
            CoalesceFactoryProduct);
        Register<NebulaeModel.Packets.Factory.BattleBase.BattleBaseSettingUpdatePacket>(p => p.PlanetId,
            p => FieldKey("bbs", p.PlanetId, p.BattleBaseId, (int)p.Event));
        Register<NebulaeModel.Packets.Factory.Belt.BeltReverseRequestPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Belt.BeltSignalIconPacket>(p => p.PlanetId,
            p => ObjectKey("bsi", p.PlanetId, p.EntityId));
        Register<NebulaeModel.Packets.Factory.Belt.BeltSignalNumberPacket>(p => p.PlanetId,
            p => ObjectKey("bsn", p.PlanetId, p.EntityId));
        Register<NebulaeModel.Packets.Factory.Belt.BeltUpdatePickupItemsPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Belt.BeltUpdatePutItemOnPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Belt.BeltUpdatePutItemsPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Belt.ConnectToMonitorPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Belt.ConnectToSpraycoaterPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.BuildDroneLaunchPacket>(null);
        Register<NebulaeModel.Packets.Factory.ConstructionLaunchOfferPacket>(null);
        Register<NebulaeModel.Packets.Factory.ConstructionLaunchGrantPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.BaseDroneStatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.DroneFlightSnapshotRequestPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.ConstructionStoragePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.RepairOfferPacket>(null);
        Register<NebulaeModel.Packets.Factory.RepairControlPacket>(null);
        Register<NebulaeModel.Packets.Factory.RepairDroneStatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.BuildEntityRequest>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.BuildingDeathPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.PrebuildInstancePacket>(p => p.PlanetId,
            p => ObjectKey("pbi", p.PlanetId, p.PrebuildId));
        // Batches are replayed in order and resolved by the monotonic per-slot map, so they need no
        // coalescing key of their own.
        Register<NebulaeModel.Packets.Factory.PrebuildInstanceBatchPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.BuildTargetAssignmentPacket>(p => p.PlanetId,
            p => ObjectKey("bta", p.PlanetId, p.PrebuildId));
        Register<NebulaeModel.Packets.Factory.BuildTargetAssignmentReplyPacket>(null);
        Register<NebulaeModel.Packets.Factory.BuildTargetBaseReleaseAckPacket>(null);
        Register<NebulaeModel.Packets.Factory.BuildTargetReadyPacket>(null);
        Register<NebulaeModel.Packets.Factory.CreatePrebuildsRequest>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.DestructEntityRequest>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Ejector.EjectorAutoOrbitUpdatePacket>(p => p.PlanetId,
            p => ObjectKey("eao", p.PlanetId, p.EjectorIndex));
        Register<NebulaeModel.Packets.Factory.Ejector.EjectorOrbitUpdatePacket>(p => p.PlanetId,
            p => ObjectKey("eor", p.PlanetId, p.EjectorIndex));
        Register<NebulaeModel.Packets.Factory.EntityBoostSwitchPacket>(p => p.PlanetId,
            p => FieldKey("ebs", p.PlanetId, p.Id, (int)p.EntityType));
        Register<NebulaeModel.Packets.Factory.EntityWarningSwitchPacket>(p => p.PlanetId,
            p => ObjectKey("ews", p.PlanetId, p.EntityId));
        Register<NebulaeModel.Packets.Factory.ExtraInfoUpdatePacket>(p => p.PlanetId,
            p => ObjectKey("eiu", p.PlanetId, p.ObjId));
        Register<NebulaeModel.Packets.Factory.Foundation.FoundationBlueprintPastePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Foundation.FoundationBuildUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Foundation.PlanetReformPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Foundation.VeinPosUpdatePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Inserter.InserterFilterUpdatePacket>(p => p.PlanetId,
            p => ObjectKey("ifu", p.PlanetId, p.InserterIndex));
        Register<NebulaeModel.Packets.Factory.Inserter.InserterItemUpdatePacket>(p => p.PlanetId,
            p => ObjectKey("iiu", p.PlanetId, p.InserterIndex));
        Register<NebulaeModel.Packets.Factory.Inserter.InserterOffsetCorrectionPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Inserter.NewSetInserterTargetPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Miner.MinerStoragePickupPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Monitor.MonitorSettingUpdatePacket>(p => p.PlanetId,
            p => FieldKey("mon", p.PlanetId, p.MonitorId, (int)p.Event));
        Register<NebulaeModel.Packets.Factory.Monitor.SpeakerSettingUpdatePacket>(p => p.PlanetId,
            p => FieldKey("spk", p.PlanetId, p.SpeakerId, (int)p.Event));
        Register<NebulaeModel.Packets.Factory.PasteBuildingSettingUpdate>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.PowerExchanger.PowerExchangerChangeModePacket>(p => p.PlanetId,
            p => ObjectKey("pxm", p.PlanetId, p.PowerExchangerIndex));
        Register<NebulaeModel.Packets.Factory.PowerExchanger.PowerExchangerStorageUpdatePacket>(p => p.PlanetId,
            p => ObjectKey("pxs", p.PlanetId, p.PowerExchangerIndex));
        Register<NebulaeModel.Packets.Factory.PowerGenerator.PowerGeneratorFuelUpdatePacket>(p => p.PlanetId,
            p => ObjectKey("pgf", p.PlanetId, p.PowerGeneratorIndex));
        Register<NebulaeModel.Packets.Factory.PowerGenerator.PowerGeneratorProductUpdatePacket>(p => p.PlanetId,
            p => ObjectKey("pgp", p.PlanetId, p.PowerGeneratorIndex));
        Register<NebulaeModel.Packets.Factory.PowerTower.PowerTowerChargerUpdate>(null);
        Register<NebulaeModel.Packets.Factory.PrebuildItemRequiredUpdate>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.PrebuildReconstructPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.RayReceiver.RayReceiverChangeLensPacket>(p => p.PlanetId,
            p => ObjectKey("rrl", p.PlanetId, p.GeneratorId));
        Register<NebulaeModel.Packets.Factory.RayReceiver.RayReceiverChangeModePacket>(p => p.PlanetId,
            p => ObjectKey("rrm", p.PlanetId, p.GeneratorId));
        Register<NebulaeModel.Packets.Factory.Splitter.SplitterFilterChangePacket>(p => p.PlanetId,
            p => ObjectKey("spf", p.PlanetId, p.SplitterIndex));
        Register<NebulaeModel.Packets.Factory.Splitter.SplitterPriorityChangePacket>(p => p.PlanetId,
            p => FieldKey("spp", p.PlanetId, p.SplitterIndex, p.Slot));
        Register<NebulaeModel.Packets.Factory.Storage.StorageSyncRealtimeChangePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Storage.StorageSyncRequestPacket>(null);
        Register<NebulaeModel.Packets.Factory.Storage.StorageSyncResponsePacket>(null);
        Register<NebulaeModel.Packets.Factory.Storage.StorageSyncSetBansPacket>(p => p.PlanetId,
            p => ObjectKey("ssb", p.PlanetId, p.StorageIndex));
        Register<NebulaeModel.Packets.Factory.Storage.StorageSyncSetFilterPacket>(p => p.PlanetId,
            p => "ssf:" + p.PlanetId + ":" + p.StorageIndex + ":" + p.GridIndex + ":" + (int)p.StorageType);
        Register<NebulaeModel.Packets.Factory.Storage.StorageSyncSortPacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Factory.Tank.SprayerStorageUpdatePacket>(p => p.PlanetId,
            p => ObjectKey("sps", p.PlanetId, p.SprayerIndex));
        Register<NebulaeModel.Packets.Factory.Tank.TankInputOutputSwitchPacket>(p => p.PlanetId,
            p => ObjectKey("tio", p.PlanetId, p.TankIndex));
        Register<NebulaeModel.Packets.Factory.Tank.TankStorageUpdatePacket>(p => p.PlanetId,
            p => ObjectKey("tst", p.PlanetId, p.TankIndex));
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
        Register<NebulaeModel.Packets.GameStates.GamePauseStepRequest>(null);
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
        Register<NebulaeModel.Packets.Logistics.ControlPanel.LCPObjectEntryEntityInfo>(null);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.LCPObjectEntryRequest>(null);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.LCPStationEntryUpdate>(null);
        Register<NebulaeModel.Packets.Logistics.ControlPanel.LCPStationRouteResultsPacket>(null);
        Register<NebulaeModel.Packets.Logistics.DispenserAddTakePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Logistics.DispenserCourierPacket>(null);
        Register<NebulaeModel.Packets.Logistics.DispenserSettingPacket>(p => p.PlanetId,
            p => FieldKey("dsp", p.PlanetId, p.DispenserId, (int)p.Event));
        Register<NebulaeModel.Packets.Logistics.DispenserStorePacket>(p => p.PlanetId);
        Register<NebulaeModel.Packets.Logistics.ILSAddStationComponent>(p => -p.PlanetId);
        Register<NebulaeModel.Packets.Logistics.ILSArriveStarPlanetRequest>(null);
        Register<NebulaeModel.Packets.Logistics.ILSArriveStarPlanetResponse>(null);
        Register<NebulaeModel.Packets.Logistics.ILSgStationPoolSync>(null);
        Register<NebulaeModel.Packets.Logistics.ILSRemoveStationComponent>(p => -p.PlanetId);
        Register<NebulaeModel.Packets.Logistics.ILSRequestStationPoolSync>(null);
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
        Register<NebulaeModel.Packets.Warning.GreenPrebuildWarningRequest>(null);
        Register<NebulaeModel.Packets.Warning.GreenPrebuildWarningState>(null);
        Register<NebulaeModel.Packets.Warning.WarningSignalPacket>(null);
    }

    public bool Contains(Type type) => policies.ContainsKey(WireContract.PacketId(type));

    private void Register<T>(Func<T, int> policy, Func<T, string> coalesce = null) where T : class, new()
    {
        var id = WireContract.PacketId(typeof(T));
        policies.Add(id, policy == null ? null : p => policy((T)p));
        if (coalesce != null) coalescers.Add(id, p => coalesce((T)p));
        if (policy != null)
            decoder.Subscribe<T>(packet => Describe(packet, currentBytes), () => new T());
    }

    // Absolute state of one object or one setting: only its latest value has to be replayed.
    // Operations that carry an event (items on a belt, construction, destruction, ...) stay
    // append-only, so they pass null here.
    private static string ObjectKey(string kind, int planet, int id) => kind + ":" + planet + ":" + id;
    private static string FieldKey(string kind, int planet, int id, int field) =>
        kind + ":" + planet + ":" + id + ":" + field;

    private static string CoalesceFactoryProduct(NebulaeModel.Packets.Factory.Assembler.FactoryProductPacket packet)
    {
        if (packet.Key == null) return null;
        var message = (NebulaeModel.Packets.Factory.Assembler.FactoryProductMessage)packet.Message;
        if (message is not (NebulaeModel.Packets.Factory.Assembler.FactoryProductMessage.Snapshot or
            NebulaeModel.Packets.Factory.Assembler.FactoryProductMessage.FighterPortsSnapshot)) return null;
        return "fpp:" + packet.Message + ":" + Convert.ToBase64String(packet.Key);
    }

    public JoinSyncBuffer.Change[] Describe<T>(T packet, byte[] bytes) where T : class
    {
        lock (policies)
        {
            if (depth == 0) changes.Clear();
            if (++depth > 8) { depth--; throw new InvalidOperationException("Nested join envelope is too deep."); }
            try
            {
                var id = WireContract.PacketId(packet.GetType());
                if (!policies.TryGetValue(id, out var policy))
                    throw new InvalidOperationException("Missing join policy for " + packet.GetType());
                var scope = policy?.Invoke(packet) ?? Ignore;
                if (scope == Envelope)
                {
                    var inner = packet is StarBroadcastPacket star ? star.PacketObjects : ((PlanetBroadcastPacket)(object)packet).PacketObjects;
                    if (inner == null || inner.Length > JoinSyncBuffer.BatchCount) throw new InvalidOperationException("Invalid join envelope.");
                    foreach (var raw in inner) Decode(raw);
                }
                else if (scope != Ignore)
                    changes.Add(new JoinSyncBuffer.Change(scope, bytes,
                        coalescers.TryGetValue(id, out var coalesce) ? coalesce(packet) : null));
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