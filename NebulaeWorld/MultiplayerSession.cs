#region

using System;
using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Logger;
using NebulaeWorld.Authority;
using NebulaeWorld.Authority.Adapters;
using NebulaeWorld.Combat;
using NebulaeWorld.Factory;
using NebulaeWorld.GameDataHistory;
using NebulaeWorld.GameStates;
using NebulaeWorld.Logistics;
using NebulaeWorld.Planet;
using NebulaeWorld.Player;
using NebulaeWorld.Statistics;
using NebulaeWorld.Trash;
using NebulaeWorld.Universe;
using NebulaeWorld.Warning;

#endregion

namespace NebulaeWorld;

internal class MultiplayerSession : IDisposable
{
    private bool canPause = true;

    public MultiplayerSession(INetworkProvider networkProvider)
    {
        Network = networkProvider;
        if (networkProvider is IServer server)
            Server = server;

        if (networkProvider is IClient client)
            Client = client;

        LocalPlayer = new LocalPlayer();
        World = new SimulatedWorld();
        Combat = new CombatManager();
        CombatAuthority = new CombatAuthorityManager();
        PlayerWeapons = new PlayerWeaponManager();
        DarkFogTurrets = new NativeDarkFogTurrets();
        FleetCommands = new FleetCommandPrediction();
        InventoryTransfers = new PlayerInventoryTransfers();
        FactoryProducts = new FactoryProductManager();
        Research = new ResearchContributionManager();
        Turrets = new TurretAuthorityManager();
        Generations = new CombatGenerationManager();
        BattleVisuals = new BattleVisualManager();
        Impacts = new BattleImpactCapture();
        Life = new PlayerLifeManager();
        Enemies = new EnemyManager();
        EnemyMotion = new EnemyMotionPresentation();
        Factories = new FactoryManager();
        Storage = new StorageManager();
        PowerTowers = new PowerTowerManager();
        Belts = new BeltManager();
        BuildTools = new BuildToolManager();
        BuildDispatch = new BuildDispatchManager();
        Drones = new DroneManager();
        Gizmos = new GizmoManager();
        History = new GameDataHistoryManager();
        State = new GameStatesManager();
        Goals = new GoalManager();
        Metadata = new MetadataManager();
        PropertyTransactions = new MetadataTransactionManager();
        Couriers = new CourierManager();
        Ships = new ILSShipManager();
        StationsUI = new StationUIManager();
        FacilityDirectory = new FacilityDirectoryManager();
        Planets = new PlanetManager();
        Statistics = new StatisticsManager();
        Kills = new KillStatisticsManager();
        Vegetation = new VegetationManager();
        Trashes = new TrashManager();
        Drops = new PersistentDropManager();
        DysonSpheres = new DysonSphereManager();
        Launch = new LaunchManager();
        Warning = new WarningManager();
        Authority = new AuthoritySessionState();
        AuthorityRuntime = new AuthoritySession(Authority) { RequireGamePools = true };
        AuthorityRuntime.AfterClientFrame = _ => { Turrets.TickClient(); FactoryProducts.TickClient(); Research.TickClient(); PlayerWeapons.TickClient(); DarkFogTurrets.Tick(); FleetCommands.Tick(); Enemies.TickClientHivePreview(); };
        // The authority rule guard needs to see this session's mode and apply window; installing here keeps
        // every caller (game patches, packet processors, world managers) on one decision point.
        AuthorityGuardWiring.Install();
    }

    public SimulatedWorld World { get; set; }
    public CombatManager Combat { get; set; }
    public CombatAuthorityManager CombatAuthority { get; set; }
    public PlayerWeaponManager PlayerWeapons { get; set; }
    public NativeDarkFogTurrets DarkFogTurrets { get; set; }
    public FleetCommandPrediction FleetCommands { get; set; }
    public TurretAuthorityManager Turrets { get; set; }
    public PlayerInventoryTransfers InventoryTransfers { get; set; }
    public FactoryProductManager FactoryProducts { get; set; }
    public ResearchContributionManager Research { get; set; }
    public CombatGenerationManager Generations { get; set; }
    public BattleVisualManager BattleVisuals { get; set; }
    public BattleImpactCapture Impacts { get; set; }
    public PlayerLifeManager Life { get; set; }
    public EnemyManager Enemies { get; set; }
    public EnemyMotionPresentation EnemyMotion { get; set; }
    public StorageManager Storage { get; set; }
    public PowerTowerManager PowerTowers { get; set; }
    public BeltManager Belts { get; set; }
    public BuildToolManager BuildTools { get; set; }
    public BuildDispatchManager BuildDispatch { get; set; }
    public DroneManager Drones { get; set; }
    public GizmoManager Gizmos { get; set; }
    public GameDataHistoryManager History { get; set; }
    public GameStatesManager State { get; set; }
    public JoinSyncManager JoinSync { get; } = new();
    public GoalManager Goals { get; set; }
    public MetadataManager Metadata { get; set; }
    public MetadataTransactionManager PropertyTransactions { get; set; }
    public CourierManager Couriers { get; set; }
    public ILSShipManager Ships { get; set; }
    public StationUIManager StationsUI { get; set; }
    public FacilityDirectoryManager FacilityDirectory { get; set; }
    public PlanetManager Planets { get; set; }
    public StatisticsManager Statistics { get; set; }
    public KillStatisticsManager Kills { get; set; }
    public VegetationManager Vegetation { get; set; }
    public TrashManager Trashes { get; set; }
    public PersistentDropManager Drops { get; set; }
    public DysonSphereManager DysonSpheres { get; set; }
    public LaunchManager Launch { get; set; }
    public WarningManager Warning { get; set; }

    /// <summary>Authority identity the packet gate validates against.</summary>
    public AuthoritySessionState Authority { get; set; }

    /// <summary>Authority runtime: command inbox, replica apply window and frame boundary.</summary>
    public AuthoritySession AuthorityRuntime { get; set; }

    public bool IsInLobby { get; set; }

    public bool CanPause
    {
        get => canPause;
        set
        {
            canPause = value;
            SimulatedWorld.SetPauseIndicator(value);
        }
    }

    /// <summary>
    ///     True while the world is frozen by a player-triggered pause (tech tree, Dyson editor, ...).
    ///     The flag is owned by the host and broadcast to every peer, so all clients stop their
    ///     logic frames together and no tick drift can accumulate. Unlike CanPause it does not
    ///     re-enable the ESC-menu pause, which stays a host-only escape hatch.
    /// </summary>
    public bool WorldPaused { get; set; }

    // A hosted session always has its human host in the world; a headless server is nobody's
    // mecha and must not reserve a player slot in the count.
    public ushort NumPlayers { get; set; } = Multiplayer.IsDedicated ? (ushort)0 : (ushort)1;

    public void Dispose()
    {
        // Client.Stop publishes and flushes the final personal checkpoint. Keep the
        // interaction gate intact until it has decided whether this player was admitted.
        try { Network?.Dispose(); }
        finally
        {
            Network = null;
            JoinSync?.Dispose();
        }

        LocalPlayer?.Dispose();
        LocalPlayer = null;

        World?.Dispose();
        World = null;

        Combat?.Dispose();
        Combat = null;
        PlayerWeapons?.Dispose();
        DarkFogTurrets?.Dispose();
        FleetCommands?.Dispose();
        PlayerWeapons = null;
        CombatAuthority?.Dispose();
        CombatAuthority = null;
        Turrets?.Dispose();
        Turrets = null;
        FactoryProducts?.Dispose();
        FactoryProducts = null;
        Research?.Dispose();
        Research = null;
        InventoryTransfers?.Dispose();
        InventoryTransfers = null;
        Generations?.Dispose();
        Generations = null;
        BattleVisuals?.Dispose();
        BattleVisuals = null;
        Impacts?.Dispose();
        Impacts = null;
        Life?.Dispose();
        Life = null;
        Vegetation?.Dispose();
        Vegetation = null;

        Enemies?.Dispose();
        Enemies = null;
        EnemyMotion?.Dispose();
        EnemyMotion = null;

        Factories?.Dispose();
        Factories = null;

        Storage?.Dispose();
        Storage = null;

        PowerTowers?.Dispose();
        PowerTowers = null;

        Belts?.Dispose();
        Belts = null;

        BuildTools?.Dispose();
        BuildTools = null;

        BuildDispatch?.Dispose();
        BuildDispatch = null;

        Drones?.Dispose();
        Drones = null;

        Gizmos?.Dispose();
        Gizmos = null;

        History?.Dispose();
        History = null;

        State?.Dispose();
        State = null;
        Goals?.Dispose();
        Goals = null;
        Metadata?.Dispose();
        Metadata = null;
        PropertyTransactions?.Dispose();
        PropertyTransactions = null;

        Couriers?.Dispose();
        Couriers = null;

        Ships = null;

        StationsUI?.Dispose();
        StationsUI = null;
        FacilityDirectory?.Dispose();
        FacilityDirectory = null;

        Planets?.Dispose();
        Planets = null;

        Statistics?.Dispose();
        Statistics = null;
        Kills?.Dispose();
        Kills = null;

        Trashes?.Dispose();
        Trashes = null;
        Drops?.Dispose();
        Drops = null;

        DysonSpheres?.Dispose();
        DysonSpheres = null;

        Launch?.Dispose();
        Launch = null;

        Warning?.Dispose();
        Warning = null;

        AuthorityRuntime?.Dispose();
        AuthorityRuntime = null;

        Authority?.Reset();
        Authority = null;

        GC.SuppressFinalize(this);
    }

    public INetworkProvider Network { get; set; }

    public IServer Server { get; set; }

    public IClient Client { get; set; }

    public ILocalPlayer LocalPlayer { get; set; }
    public FactoryManager Factories { get; set; }
    public bool IsDedicated => Multiplayer.IsDedicated;
    public bool IsServer => Server is not null;
    public bool IsClient => Client is not null;

    public bool IsGameLoaded { get; set; }

    public void OnGameLoadCompleted()
    {
        if (IsGameLoaded)
        {
            return;
        }

        Log.Info("==== Game load completed ====");
        if (IsServer) SaveManager.EnsureServerDataLoaded();
        if (IsServer) SaveManager.BindWorldIdentity(GameMain.data);
        if (IsServer)
        {
            GoalManager.RestoreLevel(GameMain.data);
            Goals.LoadLegacyDefaults();
            Goals.PrepareHostProfile();
        }
        IsGameLoaded = true;
        Planets.CompleteWorldLoad();
        if (IsServer) Metadata.Initialize();

        // A loaded host world gets its authority identity here, once, so every peer keys its
        // commands and objects to the same world. The client adopts the epoch from the host's
        // welcome instead; until then it has none and the gate refuses authority packets.
        if (IsServer)
        {
            AuthorityRuntime?.BeginAuthorityWorld(AuthorityEpoch.New(), isHost: true);

            if (AuthorityRuntime?.IsHostAuthority == true)
            {
                var epoch = AuthorityRuntime.Identity.Epoch;
                var entities = new FactoryCombatSnapshotAdapter(epoch);
                AuthorityRuntime.FactoryCombatHost = entities;
                var groundEnemies = new GroundEnemySnapshotAdapter(epoch);
                var spaceEnemies = new SpaceEnemySnapshotAdapter(epoch);
                AuthorityRuntime.GroundEnemyHost = groundEnemies;
                AuthorityRuntime.SpaceEnemyHost = spaceEnemies;
                var crafts = new CraftSnapshotAdapter(epoch);
                AuthorityRuntime.CraftHost = crafts;
                var darkFog = new DarkFogSnapshotAdapter(epoch);
                Turrets.BeginHostWorld(epoch);
                var worldView = new CompositeHostWorldView(
                    entities,
                    groundEnemies,
                    spaceEnemies,
                    crafts,
                    darkFog,
                    Turrets.HostView);
                CombatAuthority.TargetWorld = worldView;
                AuthorityRuntime.HostExecutor = CombatAuthority;
                AuthorityRuntime.BeforeHostFrame = tick =>
                {
                    Enemies.TickHivePreviews(GameMain.spaceSector);
                    CombatAuthority.BeginFrame(tick);
                };
                AuthorityRuntime.AfterHostFrame = tick =>
                {
                    CombatAuthority.CompleteFrame(tick);
                    // Workers are parked here; projectile pools must not be sampled mid-tick.
                    PlayerWeapons.HostFrame();
                    BattleVisuals.GameTick();
                };
                AuthorityRuntime.SubscriptionPlanetProvider = playerId =>
                {
                    var player = Server?.Players?.Get(playerId) ?? Server?.Players?.Get(playerId, EConnectionStatus.Syncing);
                    return player == null ? (int?)null : System.Math.Max(0, player.Data.LocalPlanetId);
                };
                AuthorityRuntime.CommandResultSink = (command, disposition, outcome) =>
                {
                    var player = Server?.Players?.Get(command.ConnectionPlayerId);
                    if (player == null || !AuthorityRuntime.ConnectionEpochFor(player.Id).Equals(command.Key.Connection)) return;
                    var header = new AuthorityEnvelopeHeader(AuthorityFamily.CommandResult,
                        Authority.Epoch, command.Key.Connection, command.Key.Sequence, GameMain.gameTick,
                        command.ConnectionPlayerId, outcome.ResultData?.Length ?? 0);
                    player.SendPacket(AuthorityCommandResultPacket.Create(header, outcome));
                };
                var networkSink = new NetworkReplicationSink(playerId =>
                    Server?.Players?.Get(playerId) ?? Server?.Players?.Get(playerId, EConnectionStatus.Syncing));
                if (!AuthorityRuntime.RegisterHostReplication(worldView, networkSink, AuthorityRuntime.HostDeaths))
                {
                    Log.Warn("[authority] host replication registration failed; the host stays fail-closed (no capture)");
                }
                else
                {
                    // 20 Hz state snapshots at 60 UPS; native combat and lifecycle still run every tick.
                    AuthorityRuntime.HostReplicator.StateIntervalTicks = 3;
                    // One predicate, two cadences: an event-driven adapter publishes on the tick a
                    // committed fact changes, so it is sampled every tick; the interval table must
                    // then answer with that same cadence, because an override may only slow a member
                    // down. Duplicating the predicate instead of sharing it would let the two drift,
                    // and a drifted table would silently throttle turrets or ground enemies.
                    bool EventDriven(ObjectKey key) => key.Kind is PoolKind.Turret or PoolKind.GroundEnemy or PoolKind.SpaceEnemy ||
                        key.Kind == PoolKind.GroundCraft && GameMain.galaxy.PlanetById(key.Scope)?.factory is { } craftFactory &&
                        key.NativeId < craftFactory.craftCursor && craftFactory.craftPool[key.NativeId].dynamic ||
                        key.Kind == PoolKind.SpaceCraft && GameMain.spaceSector is { } craftSector &&
                        key.NativeId < craftSector.craftCursor && craftSector.craftPool[key.NativeId].dynamic;
                    AuthorityRuntime.HostReplicator.EventDrivenStateFor = EventDriven;
                    // a scope none of whose subscribers is fighting is sampled at the cold
                    // cadence instead. The host decides because it runs the combat simulation, so a
                    // player who starts shooting moves their scopes back to 20 Hz in the same frame
                    // with no round trip.
                    AuthorityRuntime.HostReplicator.ColdStateIntervalTicks = 15;
                    AuthorityRuntime.HostReplicator.EngagementProvider = CombatAuthority.IsEngaged;
                    AuthorityRuntime.HostReplicator.MemberInterestFor = spaceEnemies.InterestFor;
                    AuthorityRuntime.HostReplicator.SubscriberStarProvider = playerId =>
                    {
                        var player = Server?.Players?.Get(playerId) ?? Server?.Players?.Get(playerId, EConnectionStatus.Syncing);
                        return System.Math.Max(0, player?.Data.LocalStarId ?? 0);
                    };
                    // the vanilla death commit feeds the death ledger from the one capture
                    // point, keyed by the same adapters the replication scan reads. No replication
                    // registration means no key source either, so no capture — fail-closed.
                    AuthorityRuntime.SetHostDeathCapture(new HostDeathCapture(AuthorityRuntime.HostDeaths,
                        new VanillaDeathKeyResolver(entities, groundEnemies, spaceEnemies, crafts).TryResolveKey));
                }
            }
        }

        if (Multiplayer.Session.LocalPlayer.IsHost)
        {
            GameMain.history.universeObserveLevel = SimulatedWorld.GetUniverseObserveLevel();
        }

        if (Multiplayer.Session.LocalPlayer.IsInitialDataReceived)
        {
            Multiplayer.Session.World.SetupInitialPlayerState();
        }
        if (IsServer) BuildDispatch.InitializeLoadedFactories();
    }
}
