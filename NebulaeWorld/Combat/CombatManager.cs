#region

using System;
using System.Collections.Generic;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Players;
using NebulaeWorld.MonoBehaviours.Remote;
using UnityEngine;
#pragma warning disable IDE1006 // Naming Styles
#pragma warning disable CA1822 // Mark members as static

#endregion

namespace NebulaeWorld.Combat;

internal class CombatManager : IDisposable
{
    public readonly ToggleSwitch IsIncomingRequest = new();

    public static int PlayerId { get; private set; }

    internal struct PlayerPosition
    {
        public ushort id;
        public int planetId;
        public int starId;
        public Vector3 position;
        public VectorLF3 uPosition;
        public bool isAlive;
        public Mecha mecha;
        public Vector3 skillTargetL;
        public Vector3 velocityL;
        public VectorLF3 skillTargetULast;
        public VectorLF3 skillTargetU;
    }

    public PlayerPosition[] Players; // include self
    public HashSet<int> ActivedPlanets;
    public HashSet<int> ActivedStars;
    public HashSet<int> ActivedStarsMechaInSpace; // player in the system and not on a planet
    public Dictionary<int, int> IndexByPlayerId;
    private RemotePlayerMovement.Snapshot localMovement;
    private int localMovementStar;
    private bool hasLocalMovement;

    public CombatManager()
    {
        Players = new PlayerPosition[2];
        ActivedPlanets = [];
        ActivedStars = [];
        ActivedStarsMechaInSpace = [];
        IndexByPlayerId = [];
    }

    public void Dispose()
    {
        PlayerId = 1;
        Players = null;
        ActivedPlanets = null;
        ActivedStars = null;
        ActivedStarsMechaInSpace = null;
        IndexByPlayerId = null;
        hasLocalMovement = false;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Explicit caster context for one host combat execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Vanilla routes several combat paths through this shared <see cref="PlayerId"/> global (see the
    /// <c>PlayerAction_Combat</c> transpilers). The host states, for exactly one execution, whose rules
    /// are running — and restores the previous value even when the execution throws. Construct in a
    /// <c>using</c>, run the vanilla rule for that player, and the previous id is restored on dispose.
    /// Long-lived state stays in the host simulation keyed by persistent owner, never here.
    /// </para>
    /// </remarks>
    internal sealed class CombatPlayerScope : IDisposable
    {
        private readonly int previousPlayerId;
        private bool disposed;

        internal CombatPlayerScope(ushort actingPlayerId)
        {
            previousPlayerId = PlayerId;
            ActingPlayerId = actingPlayerId;
            PlayerId = actingPlayerId;
        }

        /// <summary>The player whose rules run inside this scope.</summary>
        public ushort ActingPlayerId { get; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            PlayerId = previousPlayerId;
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Opens an explicit caster scope for one host combat execution.</summary>
    public static CombatPlayerScope CombatAs(ushort playerId) => new CombatPlayerScope(playerId);

    public void GameTick()
    {
        if (!Multiplayer.Session.IsGameLoaded) return;
        var gameTick = GameMain.gameTick;
        ActivedPlanets.Clear();
        ActivedStars.Clear();
        ActivedStarsMechaInSpace.Clear();
        IndexByPlayerId.Clear();
        using (Multiplayer.Session.World.GetRemotePlayersModels(out var remotePlayersModels))
        {
            // Mimic SkillSystem.CollectPlayerStates()
            if (Players.Length != (remotePlayersModels.Count + 1))
            {
                Players = new PlayerPosition[remotePlayersModels.Count + 1];
            }
            PlayerId = Multiplayer.Session.LocalPlayer.Id;
            Players[0].id = Multiplayer.Session.LocalPlayer.Id;
            Players[0].planetId = GameMain.localPlanet?.id ?? -1;
            Players[0].starId = GameMain.localStar?.id ?? -1;
            Players[0].position = GameMain.mainPlayer.position;
            Players[0].uPosition = GameMain.mainPlayer.uPosition;
            Players[0].velocityL = GameMain.mainPlayer.controller.velocity;
            ApplyLocalMovement(ref Players[0], GameMain.localPlanet);
            Players[0].isAlive = GameMain.mainPlayer.isAlive;
            if (Multiplayer.Session.IsDedicated) Players[0].isAlive = false;
            var mecha = GameMain.mainPlayer.mecha;
            Players[0].mecha = mecha;
            Players[0].skillTargetL = Players[0].position + (mecha.skillTargetLCenter - GameMain.mainPlayer.position);
            Players[0].skillTargetULast = Players[0].skillTargetU;
            Players[0].skillTargetU = mecha.skillTargetUCenter;

            ActivedPlanets.Add(Players[0].planetId);
            ActivedStars.Add(Players[0].starId);
            if (Players[0].planetId <= 0 && Players[0].starId > 0)
            {
                ActivedStarsMechaInSpace.Add(Players[0].starId);
            }
            IndexByPlayerId[Players[0].id] = 0;

            var index = 1;
            foreach (var pair in remotePlayersModels)
            {
                var snapshot = pair.Value.Movement.GetLastPosition();
                var planetData = GameMain.galaxy.PlanetById(snapshot.LocalPlanetId);
                var player = pair.Value.PlayerInstance;
                if (Multiplayer.Session.IsClient)
                {
                    // Remote model bones still use their render pose for visual effects.
                    // The simulation table below is populated independently from the sample.
                    player.uPosition = planetData == null ? pair.Value.Movement.absolutePosition
                        : RemoteCombatPose.UniversalPoint(planetData.uPosition, planetData.runtimeRotation, player.position);
                    if (planetData != null) player.uRotation = planetData.runtimeRotation * snapshot.BodyRotation;
                }
                ref var ptr = ref Players[index];
                ptr.id = pair.Key;
                ptr.planetId = snapshot.LocalPlanetId;
                ptr.starId = pair.Value.Movement.LocalStarId;
                SetSimulationPosition(ref ptr, snapshot, planetData);
                // Host actors already have an accepted pose at the simulation boundary. Never
                // overwrite it with the remote avatar's deliberately delayed render position.
                if (Multiplayer.Session.IsServer && Multiplayer.Session.CombatAuthority.TryGetPose(pair.Value.MechaInstance, out var pose))
                {
                    ptr.position = pose.LocalPosition;
                    ptr.uPosition = pose.UniversalPosition;
                }
                ptr.isAlive = player.isAlive;

                mecha = pair.Value.MechaInstance;
                ptr.mecha = mecha;
                ptr.skillTargetL = ptr.position + (mecha.skillTargetLCenter - player.position);
                ptr.skillTargetULast = ptr.skillTargetU;
                ptr.skillTargetU = mecha.skillTargetUCenter;

                ActivedPlanets.Add(ptr.planetId);
                ActivedStars.Add(ptr.starId);
                if (ptr.planetId <= 0 && ptr.starId > 0)
                {
                    ActivedStarsMechaInSpace.Add(ptr.starId);
                }
                IndexByPlayerId[pair.Key] = index++;

                PlayerLifeManager.TickRemote(pair.Value);
            }
        }
    }

    internal static void SetSimulationPosition(ref PlayerPosition view, in RemotePlayerMovement.Snapshot sample, PlanetData planet)
    {
        view.position = sample.LocalPlanetPosition.ToVector3();
        view.velocityL = sample.VelocityL.ToVector3();
        view.uPosition = planet == null
            ? new VectorLF3(sample.UPosition.x, sample.UPosition.y, sample.UPosition.z)
            : RemoteCombatPose.UniversalPoint(planet.uPosition, planet.runtimeRotation, view.position);
    }

    internal void RecordLocalMovement(PlayerMovement sample, int starId)
    {
        localMovement = new RemotePlayerMovement.Snapshot { LocalPlanetId = sample.LocalPlanetId,
            LocalPlanetPosition = sample.LocalPlanetPosition, UPosition = sample.UPosition, VelocityL = sample.VelocityL };
        localMovementStar = starId;
        hasLocalMovement = true;
    }

    internal bool ApplyLocalMovement(ref PlayerPosition view, PlanetData planet)
    {
        // The host and the other peers simulate against the published movement samples.
        // Feeding our own enemy AI a newer private pose makes it take a different path
        // between every pair of samples, even when the network has negligible latency.
        if (!hasLocalMovement || view.planetId != localMovement.LocalPlanetId || view.starId != localMovementStar) return false;
        SetSimulationPosition(ref view, localMovement, planet);
        return true;
    }

    public void OnFactoryLoadFinished(PlanetFactory factory)
    {
        var cursor = factory.defenseSystem.turrets.cursor;
        var buffer = factory.defenseSystem.turrets.buffer;
        for (var id = 1; id < cursor; id++)
        {
            if (buffer[id].id == id)
            {
                //Remove turretLaserContinuous
                buffer[id].projectileId = 0;
            }
        }
    }

    public void OnAstroFactoryUnload()
    {
        //Remove all projectiles
        GameMain.data.spaceSector.skillSystem.SetForNewGame();
    }
}
