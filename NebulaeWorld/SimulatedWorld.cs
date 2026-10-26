#region

using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using NebulaeModel.DataStructures;
using NebulaeModel.Contracts;
using NebulaeModel;
using NebulaeModel.Logger;
using NebulaeModel.Networking;
using NebulaeModel.Packets.GameStates;
using NebulaeModel.Packets.Players;
using NebulaeModel.Packets.Session;
using NebulaeModel.Packets.Trash;
using NebulaeModel.Packets.Universe;
using NebulaeModel.Packets.Warning;
using NebulaeModel.Utils;
using NebulaeWorld.MonoBehaviours;
using NebulaeWorld.MonoBehaviours.Local;
using NebulaeWorld.MonoBehaviours.Local.Chat;
using NebulaeWorld.MonoBehaviours.Remote;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

#endregion

namespace NebulaeWorld;

/// <summary>
///     This class keeps track of our simulated world. It holds all temporary entities like remote player models
///     and also helps to execute some remote player actions that you would want to replicate on the local client.
/// </summary>
internal class SimulatedWorld : IDisposable
{
    private readonly ThreadSafe threadSafe = new();
    private LocalPlayerMovement localPlayerMovement;
    private readonly HashSet<RemotePlayerModel> pendingDestruction = new();
    private int nameTagFrame = -10;

    internal bool IsPlayerJoining { get; set; }

    public void Dispose()
    {
        using (GetRemotePlayersModels(out var remotePlayersModels))
        {
            foreach (var model in remotePlayersModels.Values)
            {
                model.Destroy();
            }

            remotePlayersModels.Clear();
            foreach (var model in pendingDestruction) model.Destroy();
            pendingDestruction.Clear();
        }

        Object.Destroy(localPlayerMovement);
        SetPauseIndicator(true);
        GC.SuppressFinalize(this);
    }

    public Locker GetRemotePlayersModels(out Dictionary<ushort, RemotePlayerModel> remotePlayersModels)
    {
        return threadSafe.RemotePlayersModels.GetLocked(out remotePlayersModels);
    }

    public void SetupInitialPlayerState()
    {
        if (!Multiplayer.Session.IsGameLoaded)
        {
            Log.Warn("Trying to setup initial player state before the game is loaded!");
            return;
        }

        if (!Multiplayer.Session.LocalPlayer.IsInitialDataReceived)
        {
            Log.Warn("Trying to setup initial player state before the player data was received!");
            return;
        }

        var player = Multiplayer.Session.LocalPlayer as LocalPlayer;

        // If not a new client, we need to update the player position to put him where he was previously
        if (player is { IsClient: true })
        {
            // The seat's own planet defines the frame its pose is restored into. The planet the
            // loaded save left active belongs to whoever wrote that save, and mixing the two would
            // place the returning player at that seat's position instead of their own.
            var seatPlanet = SeatPlanet(player.Data.LocalPlanetId);
            GameMain.mainPlayer.planetId = player.Data.LocalPlanetId;
            if (seatPlanet == null)
            {
                GameMain.mainPlayer.uPosition =
                    new VectorLF3(player.Data.UPosition.x, player.Data.UPosition.y, player.Data.UPosition.z);
            }
            else
            {
                GameMain.mainPlayer.position = player.Data.LocalPlanetPosition.ToVector3();
                GameMain.mainPlayer.transform.localPosition = GameMain.mainPlayer.position;
                GameMain.mainPlayer.uPosition = seatPlanet.uPosition +
                    (VectorLF3)(seatPlanet.runtimeRotation * GameMain.mainPlayer.position);
            }
            // The mecha's facing is the world rotation of its "Model" child (BodyRotation); the root
            // transform only carries the gravity-aligned frame, which is identity while in space.
            // PlayerController.UpdateRotation keeps the two related by
            // model.rotation = Inverse(planet.runtimeRotation) * uRotation, so both convert back with
            // the planet's current rotation.
            GameMain.mainPlayer.transform.localRotation = Quaternion.Euler(player.Data.Rotation.ToVector3());
            GameMain.mainPlayer.uRotation = SeatPlanetRotation(player.Data.LocalPlanetId) *
                                            Quaternion.Euler(player.Data.BodyRotation.ToVector3());
            // The relative frame still names whoever wrote the save until the first
            // UniverseGameTick re-evaluates it, and everything that reads a position through it
            // before that tick - the wreckage of a dead seat, the controller's LateUpdate - would
            // otherwise resolve this seat against that stale origin.
            RestoreRelativeFrame(seatPlanet);

            // Load client's saved data from the last session.
            if (!player.IsNewPlayer)
            {
                player.Data.Mecha.UpdateMech(GameMain.mainPlayer);
                FixPlayerAfterImport();
                Multiplayer.Session.Life.RestoreLocal((PlayerData)player.Data);
                Multiplayer.Session.Vegetation.RestoreLocal(((PlayerData)player.Data).VegetableCollectionData);
            }
        }

        // Initialization on the host side after game is loaded
        NebulaeWorld.Statistics.StatisticsPreferences.Restore();
        NebulaeWorld.GameStates.GameStatesManager.RestorePersonalHistory(GameMain.history);
        NebulaeWorld.GameStates.GameStatesManager.SavePersonalHistory();
        NebulaeWorld.GameStates.LocalPlayerPreferences.RestoreDashboard();
        // The native startup Restore has finished and the session now has its final world ID.
        NebulaeWorld.GameStates.LocalPlayerPreferences.RestorePickFilters();
        Multiplayer.Session.Factories.InitializePrebuildRequests();

        if (player is { IsClient: true })
        {
            // Update player's Mecha tech bonuses
            ((MechaData)player.Data.Mecha).TechBonuses.UpdateMech(GameMain.mainPlayer.mecha);

            if (player.IsNewPlayer)
            {
                // Set mecha to full energy, shield and hp so new client won't have low stats when starting
                GameMain.mainPlayer.mecha.coreEnergy = GameMain.mainPlayer.mecha.coreEnergyCap;
                GameMain.mainPlayer.mecha.energyShieldEnergy = GameMain.mainPlayer.mecha.energyShieldCapacity;
                GameMain.mainPlayer.mecha.hp = GameMain.mainPlayer.mecha.hpMaxApplied;
                GameMain.mainPlayer.mecha.constructionModule.droneEnabled = true;
                if (GameMain.history.logisticShipWarpDrive)
                {
                    // If warp has unlocked, give new client few warpers
                    GameMain.mainPlayer.TryAddItemToPackage(1210, 5, 0, false);
                }
            }
            else
            {
                RestoreSavedFlightState((PlayerData)player.Data);
            }

            // Refresh Logistics Distributor traffic for player delivery package changes
            Multiplayer.Session.CombatAuthority.InitializePersonalBaseline((PlayerData)player.Data);
            GameMain.mainPlayer.factory?.transport.RefreshDispenserTraffic();

            // Notify the server that we are done loading the game
            var clientCert = CryptoUtils.GetPublicKey(CryptoUtils.GetOrCreateUserCert());
            Multiplayer.Session.JoinSync.MarkLoaded();
            Multiplayer.Session.Network.SendPacket(new SyncComplete(clientCert)
            { JoinGeneration = Multiplayer.Session.JoinSync.LocalGeneration });
            Multiplayer.Session.Network.SendPacket(new GameStateRequest());

            // Subscribe for the local star events
            Multiplayer.Session.Network.SendPacket(new PlayerUpdateLocalStarId(Multiplayer.Session.LocalPlayer.Id, GameMain.data.localStar?.id ?? -1));

            // Request latest warning signal
            Multiplayer.Session.Network.SendPacket(new WarningDataRequest(WarningRequestEvent.Signal));

        }

        // Set the name of local player in starmap from Icarus to user name
        if (player != null)
        {
            GameMain.mainPlayer.mecha.appearance.overrideName = " " + player.Data.Username + " ";
        }
        // Finally we need add the local player components to the player character
        localPlayerMovement = GameMain.mainPlayer.gameObject.AddComponentIfMissing<LocalPlayerMovement>();
        // ChatManager should exist continuously until the game is closed
        GameMain.mainPlayer.gameObject.AddComponentIfMissing<ChatManager>();
        // The player list is part of ChatManager's canvas overlay.
    }

    public void PublishLocalMovement() => localPlayerMovement?.Publish();

    // A returning client resumes the flight state it disconnected with: a player who logged
    // out mid-sail must not come back as a zero-velocity walker stranded in space. The warp
    // command is intentionally not restored — warp needs an active drive and drops out cleanly.
    internal static void RestoreSavedFlightState(PlayerData data)
    {
        var me = GameMain.mainPlayer;
        var savedState = data.AcceptedMovementState;
        var velocityU = data.AcceptedVelocityU;
        var velocityL = data.AcceptedVelocityL;
        var hasVelocity = Finite(velocityU.x) && Finite(velocityU.y) && Finite(velocityU.z) &&
            Finite(velocityL.x) && Finite(velocityL.y) && Finite(velocityL.z);
        if (data.LocalPlanetId == -1)
        {
            // Space has no walkable ground: anything below Fly would strand the player.
            me.movementState = savedState < EMovementState.Fly ? EMovementState.Fly : savedState;
            if (hasVelocity)
            {
                me.uVelocity = new VectorLF3(velocityU.x, velocityU.y, velocityU.z);
                me.controller.velocity = me.uVelocity;
            }
            AlignSailCamera(me);
            return;
        }
        var planet = GameMain.galaxy.PlanetById(data.LocalPlanetId);
        if (planet == null) return;
        var altitude = me.position.magnitude - planet.realRadius;
        if (savedState >= EMovementState.Fly || altitude > 5f || planet.type == EPlanetType.Gas)
        {
            me.movementState = savedState >= EMovementState.Fly ? savedState : EMovementState.Fly;
            if (hasVelocity)
            {
                var localVelocity = new Vector3(velocityL.x, velocityL.y, velocityL.z);
                me.controller.velocity = localVelocity;
                me.uVelocity = UniversalVelocityFromLocal(planet.runtimeRotation, localVelocity,
                    planet.GetUniversalVelocityAtLocalPoint(GameMain.gameTime, me.position));
            }
            if (me.movementState == EMovementState.Sail)
            {
                AlignSailCamera(me);
            }
            else if (me.movementState == EMovementState.Fly)
            {
                // SetReady left the fly action aiming at 15 m; hold the altitude we came back to.
                me.controller.actionFly.targetAltitude = Mathf.Max(altitude, 1f);
            }
        }
    }

    // PlayerController.UpdatePhysicsDirect rebuilds uVelocity from the planet-local velocity every
    // tick, so the restore has to do the same: the planet keeps spinning while the client is gone,
    // and replaying the frozen universal sample would aim the mecha wherever the planet has spun
    // to since logout.
    internal static VectorLF3 UniversalVelocityFromLocal(Quaternion planetRotation, Vector3 localVelocity,
        VectorLF3 surfaceVelocity) => (VectorLF3)(planetRotation * localVelocity) + surfaceVelocity;

    // The seat's planet decides which rotation converts a planet-local pose into a universal one;
    // the seat's own planet is used rather than GameMain.localPlanet because the client may be
    // arriving on a different planet than the one the loaded game data still names.
    private static Quaternion SeatPlanetRotation(int planetId) =>
        planetId > 0 ? GameMain.galaxy.PlanetById(planetId)?.runtimeRotation ?? Quaternion.identity : Quaternion.identity;

    // The planet under the restored seat, or null while that seat is in space. An id the galaxy
    // does not know means the same thing as -1: there is no planet to restore the pose against.
    internal static PlanetData SeatPlanet(int planetId) =>
        planetId > 0 && GameMain.galaxy != null ? GameMain.galaxy.PlanetById(planetId) : null;

    // GameData.DetermineRelative() reads localPlanet, which is still the save writer's planet until
    // the first UniverseGameTick re-evaluates it from the restored seat. A seat that comes back on
    // another planet must not inherit that frame, so the frame is written from the seat's own planet
    // here; a seat in space follows the seat itself, exactly as the per-tick call would leave it.
    internal static void RestoreRelativeFrame(PlanetData seatPlanet)
    {
        if (seatPlanet == null)
        {
            GameMain.data.relativePos = GameMain.mainPlayer.uPosition;
            GameMain.data.relativeRot = Quaternion.identity;
            return;
        }
        GameMain.data.relativePos = seatPlanet.uPosition;
        GameMain.data.relativeRot = seatPlanet.runtimeRotation;
    }

    // Sail mode takes both the camera pose and the thrust basis from sailPoser.targetURot
    // (PlayerMove_Sail builds the WASD directions from the camera's forward ray and that
    // rotation). The game only aligns it on a Fly<->Sail transition via SyncForSailMode, so
    // after a rejoin it is still whatever the load left it at and the mecha accelerates along
    // an axis unrelated to its heading. Point it at the facing we just restored.
    private static void AlignSailCamera(global::Player me)
    {
        if (me.movementState != EMovementState.Sail) return;
        var poser = GameCamera.instance?.sailPoser;
        // Only plain fields are written below, so a reference check is enough here — and unlike a
        // Unity null check it also covers a poser whose native side is already gone.
        if (poser is null) return;
        poser.targetURot = me.uRotation;
        poser.targetURotWanted = me.uRotation;
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    public static void FixPlayerAfterImport()
    {
        var player = GameMain.mainPlayer;

        // Mimic MechaForge.Init(Mecha _mecha)
        player.mecha.forge.SetHiddenProperty(nameof(MechaForge.mecha), GameMain.mainPlayer.mecha);
        player.mecha.forge.SetHiddenProperty(nameof(MechaForge.player), GameMain.mainPlayer);
        player.mecha.forge.gameHistory = GameMain.data.history;
        player.mecha.forge.gameHistory = GameMain.data.history;
        player.mecha.forge.bottleneckItems = new HashSet<int>();

        // Rebind runtime state without replacing the construction preferences just imported.
        player.mecha.energyShieldBurstUnlocked = true;
        // Personal saves retain local pool slots. Resolve targets again in this connection's
        // factory/claim generation instead of replaying queues from the previous connection.
        var construction = player.mecha.constructionModule;
        if (construction.buildTargets != null) Array.Clear(construction.buildTargets, 0, construction.buildTargets.Length);
        if (construction.tmpTargets != null) Array.Clear(construction.tmpTargets, 0, construction.tmpTargets.Length);
        if (construction.repairTargets != null) Array.Clear(construction.repairTargets, 0, construction.repairTargets.Length);
        if (construction.autoReconstructTargets != null) Array.Clear(construction.autoReconstructTargets, 0, construction.autoReconstructTargets.Length);
        construction.buildTargetTotalCount = construction.repairTargetTotalCount = construction.autoReconstructTargetTotalCount = 1;

        // Inventory Capacity level 7 will increase package columncount from 10 -> 12
        var packageRowCount = (player.package.size - 1) / player.GetPackageColumnCount() + 1;
        // Make sure all slots are available on UI
        player.package.SetSize(player.packageColCount * packageRowCount);
        player.deliveryPackage.rowCount = packageRowCount;
        player.deliveryPackage.NotifySizeChange();

        // do we need to do something about the spaceSector?
        player.mecha.groundCombatModule.AfterImport(GameMain.data);
        player.mecha.spaceCombatModule.AfterImport(GameMain.data);

        // Set fleetId = 0, fleetAstroId = 0 and fighter.craftId = 0
        var moduleFleets = player.mecha.groundCombatModule.moduleFleets;
        for (var index = 0; index < moduleFleets.Length; index++)
        {
            moduleFleets[index].ClearFleetForeignKey();
        }
        moduleFleets = player.mecha.spaceCombatModule.moduleFleets;
        for (var index = 0; index < moduleFleets.Length; index++)
        {
            moduleFleets[index].ClearFleetForeignKey();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter",
        Justification = "Keep the existing public join notification signature compatible with callers.")]
    public void OnPlayerJoining(string username)
    {
        IsPlayerJoining = true;
    }

    public static void OnPlayerJoinedGame(INebulaePlayer player)
    {
        Multiplayer.Session.World.SpawnRemotePlayerModel(player.Data, true);


        // Sync overrideName of planets and stars
        player.SendPacket(new NameInputPacket(GameMain.galaxy));

        // Initialize the new player's view without interrupting anybody else's charging.
        Multiplayer.Session.PowerTowers.SendSnapshot(player.Connection);

        // Sync enemyDropBans for joined client
        using (var writer = new BinaryUtils.Writer())
        {
            writer.BinaryWriter.Write(GameMain.data.trashSystem.enemyDropBans.Count);
            foreach (var itemId in GameMain.data.trashSystem.enemyDropBans)
            {
                writer.BinaryWriter.Write(itemId);
            }
            player.SendPacket(new TrashSystemLootFilterPacket(writer.CloseAndGetBytes()));
        }

        // (Host only) Trigger when a new client added to connected players
        Log.Info($"Client{player.Data.PlayerId} - {player.Data.Username} joined");
    }

    public static void OnPlayerLeftGame(INebulaePlayer player)
    {
        Multiplayer.Session.World.DestroyRemotePlayerModel(player.Id, true);

        // (Host only) Trigger when a connected client leave the game
        Log.Info($"Client{player.Data.PlayerId} - {player.Data.Username} left");
    }

    public void OnAllPlayersSyncCompleted()
    {
        IsPlayerJoining = false;
        Multiplayer.Session.CanPause = false;
        SetPauseIndicator(Multiplayer.Session.WorldPaused);
    }

    /// <summary>
    ///     Apply the host-authoritative world pause on this peer. The host runs it before
    ///     broadcasting, every client runs it when the broadcast arrives, so all peers freeze
    ///     and resume on the same tick and no drift can accumulate.
    /// </summary>
    public void SetWorldPause(bool paused, long stepTick = 0)
    {
        Multiplayer.Session.WorldPaused = paused;
        Multiplayer.Session.WorldPauseStepTick = paused ? Math.Max(0, stepTick) : 0;
        GameMain.isFullscreenPaused = paused;
        SetPauseIndicator(paused || Multiplayer.Session.CanPause);
    }

    /// <summary>
    ///     Host-side entry point for a world pause change: apply it locally and broadcast the
    ///     authoritative state to every client.
    /// </summary>
    public void HostSetWorldPause(bool paused)
    {
        // Peers behind the host may finish only up to this pause boundary.
        var stepTick = paused ? Math.Max(GameMain.gameTick, Multiplayer.Session.WorldPaused ? Multiplayer.Session.WorldPauseStepTick : 0) : 0;
        SetWorldPause(paused, stepTick);
        Multiplayer.Session.Server?.SendPacket(new GamePausePacket(paused, stepTick));
    }

    public void HostStepWorld()
    {
        var session = Multiplayer.Session;
        if (!session.WorldPaused) return;
        var limit = Math.Max(GameMain.gameTick, session.WorldPauseStepTick) + 1;
        SetWorldPause(true, limit);
        session.Server?.SendPacket(new GamePausePacket(true, limit));
    }

    /// <summary>
    ///     Prefix of the vanilla pause toggles (tech tree, Dyson editor) on a client: the local
    ///     flag is owned by the broadcast, so ask the host instead of writing it.
    /// </summary>
    public static void RequestWorldPause()
    {
        // Vanilla ignores the toggle while the mecha is fast travelling between systems.
        if (GameMain.mainPlayer.fastTravelling)
        {
            return;
        }
        Multiplayer.Session.Network.SendPacket(new GamePausePacket(!GameMain.isFullscreenPaused));
    }

    /// <summary>
    ///     Postfix of the vanilla pause toggles. A client already sent its request in the
    ///     prefix; the host applies the state vanilla just toggled and broadcasts it.
    /// </summary>
    public static void SyncWorldPauseAfterToggle()
    {
        var session = Multiplayer.Session;
        if (session == null)
        {
            return;
        }
        if (session.LocalPlayer.IsClient)
        {
            return;
        }
        session.World.HostSetWorldPause(GameMain.isFullscreenPaused);
    }

    /// <summary>
    ///     Postfix of the vanilla window closes that clear the pause flag (tech tree, Dyson
    ///     editor). Vanilla resumes the game when the window goes away, so in multiplayer the
    ///     same resume is requested from the host to keep every peer in step.
    /// </summary>
    public static void SyncWorldPauseAfterWindowClosed(bool preserveDuringFastTravel = true)
    {
        var session = Multiplayer.Session;
        if (session == null)
        {
            return;
        }
        // A client must retain the broadcast flag until its resume request is accepted.
        GameMain.isFullscreenPaused = session.WorldPaused;
        if (preserveDuringFastTravel && GameMain.mainPlayer?.fastTravelling == true || !session.WorldPaused)
        {
            return;
        }
        if (session.LocalPlayer.IsClient)
        {
            session.Network.SendPacket(new GamePausePacket(false));
        }
        else
        {
            session.World.HostSetWorldPause(false);
        }
    }

    public void SpawnRemotePlayerModel(IPlayerData playerData, bool announcePresence = false)
    {
        using (GetRemotePlayersModels(out var remotePlayersModels))
        {
            if (remotePlayersModels.ContainsKey(playerData.PlayerId))
            {
                return;
            }
            var model = new RemotePlayerModel(playerData.PlayerId, playerData.Username);
            model.Movement.LocalStarId = playerData.LocalStarId;
            model.Movement.localPlanetId = playerData.LocalPlanetId;
            remotePlayersModels.Add(playerData.PlayerId, model);
            Multiplayer.Session.Life.ApplyRemote(playerData.PlayerId, ((PlayerData)playerData).Life);

            // The snapshot sent when this client finishes loading lists everyone already in the
            // game. Only a player who actually arrives afterwards is announced.
            if (announcePresence) ChatManager.Instance?.NotifyPlayerPresence(playerData.Username, true);
        }
    }

    public void DestroyRemotePlayerModel(ushort playerId, bool announcePresence = false)
    {
        using (GetRemotePlayersModels(out var remotePlayersModels))
        {
            if (!remotePlayersModels.TryGetValue(playerId, out var player))
            {
                return;
            }
            if (announcePresence) ChatManager.Instance?.NotifyPlayerPresence(player.Username, false);
            remotePlayersModels.Remove(playerId);
            // The transport may retire the connection while combat workers still reference its mecha.
            // Keep the captured model alive until the next barrier, independently of a reused player id.
            var runtime = Multiplayer.Session?.AuthorityRuntime;
            if (runtime?.Identity.IsActive == true && Multiplayer.Session.IsGameLoaded)
            {
                pendingDestruction.Add(player);
                runtime.EnqueueLifecycle(() =>
                {
                    using (GetRemotePlayersModels(out _))
                        if (!pendingDestruction.Remove(player)) return;
                    Combat.LocalSkillLifetime.RemovePlayerReferences(GameMain.spaceSector?.skillSystem, playerId);
                    player.Destroy();
                });
            }
            else player.Destroy();
            if (remotePlayersModels.Count == 0 && Config.Options.AutoPauseEnabled)
            {
                Multiplayer.Session.CanPause = true;
            }
        }
    }

    public void UpdateRemotePlayerRealtimeState(PlayerMovementDisplay packet)
    {
        using (GetRemotePlayersModels(out var remotePlayersModels))
        {
            if (!remotePlayersModels.TryGetValue(packet.PlayerId, out var player))
            {
                return;
            }
            player.Movement.UpdatePosition(packet);
            player.Animator.UpdateState(packet);

            // Flags belong to the animation snapshot. energyShieldEnergyRate is the native
            // joules-per-damage conversion (normally 500), never a has-shield marker.
        }
    }

    public void RenderPlayerNameTagsOnStarmap(UIStarmap starmap)
    {
        // Make a copy of the "Icarus" text from the starmap
        var starmap_playerNameText = starmap.playerNameText;
        var starmap_playerTrack = starmap.playerTrack;

        using (GetRemotePlayersModels(out var remotePlayersModels))
        {
            if (remotePlayersModels.Count == 0) return;
            foreach (var playerModel in remotePlayersModels.Values)
            {
                Text nameText;
                Transform starmapTracker;
                if (playerModel.StarmapNameText != null && playerModel.StarmapTracker != null)
                {
                    nameText = playerModel.StarmapNameText;
                    starmapTracker = playerModel.StarmapTracker;
                }
                else
                {
                    // Make an instance of the "Icarus" text to represent the other player name
                    nameText = playerModel.StarmapNameText =
                        Object.Instantiate(starmap_playerNameText, starmap_playerNameText.transform.parent);
                    nameText.text = $"{playerModel.Username}";
                    nameText.gameObject.SetActive(true);

                    // Make an instance the player tracker object
                    starmapTracker = playerModel.StarmapTracker =
                        Object.Instantiate(starmap_playerTrack, starmap_playerTrack.parent);
                    starmapTracker.gameObject.SetActive(true);
                }

                // One lookup per player per frame serves both the position and the rotation below.
                var localPlanetId = playerModel.Movement.localPlanetId;
                var planet = localPlanetId > 0 ? GameMain.galaxy.PlanetById(localPlanetId) : null;

                VectorLF3 adjustedVector;
                if (localPlanetId > 0)
                {
                    // Get the position of the planet
                    adjustedVector = planet.uPosition;

                    // Add the local position of the player
                    var localPlanetPosition = playerModel.Movement.GetLastPosition().LocalPlanetPosition.ToVector3();
                    adjustedVector += (VectorLF3)Maths.QRotate(planet.runtimeRotation, localPlanetPosition);
                }
                else
                {
                    // Just use the raw uPos as we don't care too much about precise locations
                    adjustedVector = playerModel.Movement.absolutePosition;
                }

                // Scale as required
                adjustedVector = (adjustedVector - starmap.viewTargetUPos) * 0.00025;

                // Get the point on the screen that represents the world position
                if (!starmap.WorldPointIntoScreen(adjustedVector, out var rectPoint))
                {
                    continue;
                }

                // Put the marker directly on the location of the player
                starmapTracker.position = adjustedVector;

                if (localPlanetId > 0)
                {
                    var rotation = planet.runtimeRotation *
                                   Quaternion.LookRotation(playerModel.PlayerModelTransform.forward,
                                       playerModel.Movement.GetLastPosition().LocalPlanetPosition.ToVector3());
                    starmapTracker.rotation = rotation;
                }
                else
                {
                    var rotation = Quaternion.LookRotation(playerModel.PlayerModelTransform.forward,
                        playerModel.PlayerTransform.localPosition);
                    starmapTracker.rotation = rotation;
                }

                starmapTracker.localScale = UIStarmap.isChangingToMilkyWay
                    ? Vector3.zero
                    : Vector3.one * (starmap.screenCamera.transform.position - starmapTracker.position).magnitude;

                // Put their name above or below it. Skip the anchor write when the point has
                // not moved, and only flip the active state when it actually changes.
                var anchoredPosition = new Vector2(rectPoint.x + (rectPoint.x > 600f ? -35 : 35),
                    rectPoint.y + (rectPoint.y > -350.0 ? -19f : 19f));
                if (nameText.rectTransform.anchoredPosition != anchoredPosition)
                    nameText.rectTransform.anchoredPosition = anchoredPosition;
                if (nameText.gameObject.activeSelf == UIStarmap.isChangingToMilkyWay)
                    nameText.gameObject.SetActive(!UIStarmap.isChangingToMilkyWay);
            }
        }
    }

    public void ClearPlayerNameTagsOnStarmap()
    {
        using (GetRemotePlayersModels(out var remotePlayersModels))
        {
            foreach (var player in remotePlayersModels)
            {
                // Destroy the marker and name so they don't linger and cause problems
                Object.Destroy(player.Value.StarmapNameText.gameObject);
                Object.Destroy(player.Value.StarmapTracker.gameObject);

                // Null them out so they can be recreated next time the map is opened
                player.Value.StarmapNameText = null;
                player.Value.StarmapTracker = null;
            }
        }
    }

    public void RenderPlayerNameTagsInGame()
    {
        // Creating a tag is one-shot per remote player, so the dictionary only needs to be
        // scanned a few times per second instead of on every rendered frame.
        if (Time.frameCount - nameTagFrame < 10) return;
        nameTagFrame = Time.frameCount;
        using (GetRemotePlayersModels(out var remotePlayersModels))
        {
            if (remotePlayersModels.Count == 0) return;
            foreach (var playerModel in remotePlayersModels.Values)
            {
                if (playerModel.InGameNameText != null || playerModel.PlayerTransform == null)
                {
                    continue;
                }

                var sourceText = UIRoot.instance?.uiGame?.sailIndicator?.targetText;
                if (sourceText == null || GameCamera.main == null)
                {
                    continue;
                }

                var tagObject = new GameObject("Nebulae Player Name Tag");
                tagObject.transform.SetParent(playerModel.PlayerTransform, false);
                var tag = tagObject.AddComponent<RemotePlayerNameTag>();
                if (tag.Initialize(playerModel, sourceText.font))
                {
                    playerModel.InGameNameText = tag.NameText;
                }
                else
                {
                    Object.Destroy(tagObject);
                }
            }
        }
    }

    public static void SetPauseIndicator(bool canPause)
    {
        //Tell the user if the game is paused or not
        // Without a UI root there is no Esc menu to label (main menu, headless server, tests).
        if (UIRoot.instance == null)
        {
            return;
        }
        var targetObject = FindEscMenuPauseText();
        if (targetObject == null)
        {
            return;
        }
        var pauseText = targetObject.GetComponent<Text>();
        var pauseLocalizer = targetObject.GetComponent<Localizer>();
        if (!pauseText || !pauseLocalizer)
        {
            return;
        }
        if (!canPause)
        {
            pauseText.text = "--  Nebulae Multiplayer  --".Translate();
            pauseLocalizer.stringKey = "--  Nebulae Multiplayer  --";
        }
        else
        {
            pauseText.text = "游戏已暂停".Translate();
            pauseLocalizer.stringKey = "游戏已暂停";
        }
    }

    // Isolated, and kept out of the caller's JIT on purpose: GameObject.Find is a Unity internal
    // call, and inlining it would make every caller uncompilable wherever the engine runtime is
    // not available (headless server, tests).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static GameObject FindEscMenuPauseText()
    {
        return GameObject.Find("UI Root/Overlay Canvas/In Game/Esc Menu/pause-text");
    }

    public static int GetUniverseObserveLevel()
    {
        var level = 0;
        // the tech ids of the 4 tiers of Universe Exploration from https://dsp-wiki.com/Upgrades
        for (var i = 4104; i >= 4101; i--)
        {
            if (!GameMain.history.TechUnlocked(i))
            {
                continue;
            }
            // set level to last digit of tech id
            level = i % 10;
            break;
        }
        return level;
    }

    private sealed class ThreadSafe
    {
        internal readonly Dictionary<ushort, RemotePlayerModel> RemotePlayersModels = new();
    }
}
