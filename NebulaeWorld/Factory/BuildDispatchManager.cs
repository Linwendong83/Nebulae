using System;
using System.Collections.Generic;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Factory;
using UnityEngine;

namespace NebulaeWorld.Factory;

/// <summary>Coordinates ownership of green prebuilds; vanilla modules still order and fly their drones.</summary>
internal sealed class BuildDispatchManager : IDisposable
{
    private readonly BuildTargetClaims claims = new();
    private readonly HashSet<(int PlanetId, int PrebuildId)> pending = new();
    private readonly HashSet<PlanetFactory> seededFactories = new();
    private readonly Dictionary<ushort, BuilderCapability> remoteBuilders = new();
    private readonly HashSet<(int PlanetId, int PrebuildId, long Generation)> awaitingReply = new();
    private readonly Dictionary<(int PlanetId, int PrebuildId), HashSet<int>> rejectedPlayers = new();
    private readonly HashSet<(int PlanetId, int PrebuildId)> declinedLocally = new();
    private readonly HashSet<(int PlanetId, int PrebuildId, long Generation)> localBaseLaunched = new();
    private readonly HashSet<(int PlanetId, int PrebuildId, long Generation)> renderedRemoteLaunches = new();
    private readonly HashSet<(int PlanetId, int PrebuildId, long Generation)> releaseSent = new();
    private readonly Dictionary<(int PlanetId, int PrebuildId, long Generation), HashSet<ushort>> baseRevocations = new();
    private readonly HashSet<(int PlanetId, int PrebuildId, long Generation)> pendingBaseAcks = new();
    private readonly Dictionary<(int PlanetId, int PrebuildId, long Generation), long> baseLowEnergySince = new();

    public void Dispose()
    {
        OnFactoriesUnloaded();
        remoteBuilders.Clear();
        GC.SuppressFinalize(this);
    }

    public void OnFactoriesUnloaded()
    {
        claims.Clear();
        pending.Clear();
        seededFactories.Clear();
        awaitingReply.Clear();
        rejectedPlayers.Clear();
        declinedLocally.Clear();
        localBaseLaunched.Clear();
        renderedRemoteLaunches.Clear();
        releaseSent.Clear();
        baseRevocations.Clear();
        pendingBaseAcks.Clear();
        baseLowEnergySince.Clear();
    }

    public bool TryGet(int planetId, int prebuildId, out BuildTargetClaim claim) =>
        claims.TryGet(planetId, prebuildId, out claim);

    public static PlanetFactory FindFactory(PrebuildData[] pool)
    {
        var data = GameMain.data;
        if (data == null || pool == null) return null;
        for (var i = 0; i < data.factoryCount; i++)
        {
            var factory = data.factories[i];
            if (factory != null && ReferenceEquals(factory.prebuildPool, pool)) return factory;
        }
        return null;
    }

    public void ReadyLocally(PlanetFactory factory, int prebuildId)
    {
        if (factory == null) return;
        if (Multiplayer.Session.IsServer) ObserveReady(factory, prebuildId);
        if (claims.TryGet(factory.planetId, prebuildId, out var claim) &&
            (!claim.Launched || claim.OwnerKind == BuildOwnerKind.Base &&
             !localBaseLaunched.Contains((factory.planetId, prebuildId, claim.Generation))))
            RefreshLocalQueue(factory, claim);
    }

    public bool CanQueue(PlanetFactory factory, ConstructionModuleComponent module, int prebuildId)
    {
        if (factory == null || module == null || prebuildId <= 0 || prebuildId >= factory.prebuildCursor ||
            !claims.TryGet(factory.planetId, prebuildId, out var claim)) return false;
        if (baseRevocations.ContainsKey((factory.planetId, prebuildId, claim.Generation))) return false;
        if (claim.Launched && (claim.OwnerKind != BuildOwnerKind.Base ||
            localBaseLaunched.Contains((factory.planetId, prebuildId, claim.Generation)))) return false;
        ref var prebuild = ref factory.prebuildPool[prebuildId];
        if (prebuild.id != prebuildId || prebuild.itemRequired != 0 || prebuild.isDestroyed || prebuild.builderLaunched)
            return false;
        if (!module.droneEnabled || !module.droneConstructEnabled || module.droneCount <= 0) return false;
        if (module.entityId != 0)
            return claim.OwnerKind == BuildOwnerKind.Base && claim.OwnerId == module.entityId;
        if (claim.OwnerKind != BuildOwnerKind.Player || claim.OwnerId != Multiplayer.Session.LocalPlayer.Id ||
            factory != GameMain.mainPlayer.factory || GameMain.mainPlayer.speed > 20f) return false;
        return (prebuild.pos - GameMain.mainPlayer.position).sqrMagnitude <=
               GameMain.mainPlayer.mecha.buildArea * GameMain.mainPlayer.mecha.buildArea;
    }

    public void ObserveReady(PlanetFactory factory, int prebuildId)
    {
        if (!Multiplayer.Session.IsServer || !IsReady(factory, prebuildId) ||
            claims.TryGet(factory.planetId, prebuildId, out _)) return;
        pending.Add((factory.planetId, prebuildId));
        if (Multiplayer.Session.IsGameLoaded) TryAssign(factory, prebuildId);
    }

    public void Tick()
    {
        if (!Multiplayer.Session.IsGameLoaded || GameMain.galaxy == null) return;
        if (!Multiplayer.Session.IsServer)
        {
            TickClient();
            return;
        }
        if (GameMain.data == null) return;
        var data = GameMain.data;
        for (var i = 0; i < data.factoryCount; i++)
        {
            var factory = data.factories[i];
            if (factory == null || !seededFactories.Add(factory)) continue;
            SeedFactory(factory);
            ClearQueuedTargets(factory);
        }
        if (GameMain.gameTick % 30 != 0) return;
        FinishBaseRevocations();
        var invalid = new List<BuildTargetClaim>();
        foreach (var claim in claims.All)
        {
            if (claim.Launched || baseRevocations.ContainsKey((claim.PlanetId, claim.PrebuildId, claim.Generation)))
                continue;
            var factory = GameMain.galaxy.PlanetById(claim.PlanetId)?.factory;
            var baseInvalid = false;
            if (factory != null && claim.OwnerKind == BuildOwnerKind.Base)
            {
                var key = (claim.PlanetId, claim.PrebuildId, claim.Generation);
                baseInvalid = !BaseStillAvailable(factory, claim.OwnerId);
                if (!baseInvalid && !BaseHasLaunchEnergy(factory, claim.OwnerId))
                {
                    if (!baseLowEnergySince.TryGetValue(key, out var since))
                        baseLowEnergySince[key] = GameMain.gameTick;
                    else baseInvalid = GameMain.gameTick - since >= 120;
                }
                else baseLowEnergySince.Remove(key);
            }
            if (factory == null || !IsReady(factory, claim.PrebuildId) ||
                claim.OwnerKind == BuildOwnerKind.Player && claim.OwnerId == Multiplayer.Session.LocalPlayer.Id &&
                !CanQueue(factory, GameMain.mainPlayer.mecha.constructionModule, claim.PrebuildId) ||
                baseInvalid)
                invalid.Add(claim);
        }
        foreach (var claim in invalid)
        {
            if (claim.OwnerKind == BuildOwnerKind.Base) BeginBaseRevocation(claim);
            else Release(claim, retry: true);
        }
        if (pending.Count == 0) return;
        var tasks = new List<(int PlanetId, int PrebuildId)>(pending);
        foreach (var task in tasks)
        {
            var factory = GameMain.galaxy.PlanetById(task.PlanetId)?.factory;
            if (!IsReady(factory, task.PrebuildId)) pending.Remove(task);
            else TryAssign(factory, task.PrebuildId);
        }
    }

    private static bool BaseStillAvailable(PlanetFactory factory, int entityId)
    {
        if (entityId <= 0 || entityId >= factory.entityCursor || factory.entityPool[entityId].id != entityId)
            return false;
        var moduleId = factory.entityPool[entityId].constructionModuleId;
        if (moduleId <= 0 || moduleId >= factory.constructionSystem.constructionModules.cursor) return false;
        var module = factory.constructionSystem.constructionModules.buffer[moduleId];
        if (module == null || !module.droneEnabled || !module.droneConstructEnabled || module.droneCount <= 0 ||
            module.battleBaseId <= 0 || module.battleBaseId >= factory.defenseSystem.battleBases.cursor)
            return false;
        var baseComponent = factory.defenseSystem.battleBases.buffer[module.battleBaseId];
        return baseComponent != null && baseComponent.id == module.battleBaseId;
    }

    private static bool BaseHasLaunchEnergy(PlanetFactory factory, int entityId)
    {
        var moduleId = factory.entityPool[entityId].constructionModuleId;
        var module = factory.constructionSystem.constructionModules.buffer[moduleId];
        return factory.defenseSystem.battleBases.buffer[module.battleBaseId].energy >=
               Configs.freeMode.droneEjectEnergy;
    }

    private void BeginBaseRevocation(BuildTargetClaim claim)
    {
        var key = (claim.PlanetId, claim.PrebuildId, claim.Generation);
        if (baseRevocations.ContainsKey(key)) return;
        var waiting = new HashSet<ushort>();
        var factory = GameMain.galaxy.PlanetById(claim.PlanetId)?.factory;
        if (factory != null)
        {
            foreach (var entry in Multiplayer.Session.Server.Players.Connected)
                if (entry.Value.Data.LocalStarId == factory.planet.star.id) waiting.Add(entry.Value.Id);
            baseRevocations[key] = waiting;
            Multiplayer.Session.Network.SendPacketToStar(new BuildTargetAssignmentPacket(claim,
                    removed: true, canceled: true),
                factory.planet.star.id);
            RefreshLocalQueue(factory, claim);
        }
        else Release(claim, retry: false);
    }

    private void FinishBaseRevocations()
    {
        if (baseRevocations.Count == 0) return;
        var finished = new List<BuildTargetClaim>();
        foreach (var entry in baseRevocations)
        {
            if (entry.Value.Count != 0 || HasBaseDroneTarget(
                    GameMain.galaxy.PlanetById(entry.Key.PlanetId)?.factory, entry.Key.PrebuildId)) continue;
            if (claims.TryGet(entry.Key.PlanetId, entry.Key.PrebuildId, out var claim) &&
                claim.Generation == entry.Key.Generation) finished.Add(claim);
        }
        foreach (var claim in finished) Release(claim, retry: true);
    }

    private static bool HasBaseDroneTarget(PlanetFactory factory, int prebuildId)
    {
        if (factory == null) return false;
        var drones = factory.constructionSystem.drones;
        for (var i = 1; i < drones.cursor; i++)
        {
            ref var drone = ref drones.buffer[i];
            if (drone.id != i || drone.owner <= 0 || drone.stage == 0 || drone.stage == 4) continue;
            if (drone.targetObjectId == -prebuildId || drone.nextTarget1ObjectId == -prebuildId ||
                drone.nextTarget2ObjectId == -prebuildId || drone.nextTarget3ObjectId == -prebuildId)
                return true;
        }
        return false;
    }

    public void HandleBaseReleaseAck(ushort playerId, BuildTargetBaseReleaseAckPacket packet)
    {
        if (!Multiplayer.Session.IsServer) return;
        if (baseRevocations.TryGetValue((packet.PlanetId, packet.PrebuildId, packet.Generation), out var waiting))
            waiting.Remove(playerId);
    }

    private void TickClient()
    {
        if (GameMain.gameTick % 30 != 0 || GameMain.mainPlayer == null) return;
        if (pendingBaseAcks.Count > 0)
        {
            var released = new List<(int PlanetId, int PrebuildId, long Generation)>();
            foreach (var key in pendingBaseAcks)
            {
                var factory = GameMain.galaxy.PlanetById(key.PlanetId)?.factory;
                if (HasBaseDroneTarget(factory, key.PrebuildId)) continue;
                Multiplayer.Session.Network.SendPacket(new BuildTargetBaseReleaseAckPacket(
                    key.PlanetId, key.PrebuildId, key.Generation));
                released.Add(key);
            }
            foreach (var key in released) pendingBaseAcks.Remove(key);
        }
        var current = new List<BuildTargetClaim>();
        foreach (var claim in claims.All)
            if (claim.OwnerKind == BuildOwnerKind.Player && claim.OwnerId == Multiplayer.Session.LocalPlayer.Id &&
                !claim.Launched) current.Add(claim);
        foreach (var claim in current)
        {
            var factory = GameMain.galaxy.PlanetById(claim.PlanetId)?.factory;
            if (CanQueue(factory, GameMain.mainPlayer.mecha.constructionModule, claim.PrebuildId)) continue;
            var key = (claim.PlanetId, claim.PrebuildId, claim.Generation);
            if (!releaseSent.Add(key)) continue;
            declinedLocally.Add((claim.PlanetId, claim.PrebuildId));
            Multiplayer.Session.Network.SendPacket(new BuildTargetAssignmentReplyPacket(
                claim.PlanetId, claim.PrebuildId, claim.Generation, accepted: false));
            claims.Remove(claim.PlanetId, claim.PrebuildId, claim.Generation);
            RefreshLocalQueue(factory, claim);
        }
        if (declinedLocally.Count == 0) return;
        var ready = new List<(int PlanetId, int PrebuildId)>();
        foreach (var target in declinedLocally)
        {
            var factory = GameMain.galaxy.PlanetById(target.PlanetId)?.factory;
            if (!IsReady(factory, target.PrebuildId) || GameMain.mainPlayer.factory != factory) continue;
            var module = GameMain.mainPlayer.mecha.constructionModule;
            var distance = (factory.prebuildPool[target.PrebuildId].pos - GameMain.mainPlayer.position).sqrMagnitude;
            if (module.droneEnabled && module.droneConstructEnabled && module.droneCount > 0 &&
                GameMain.mainPlayer.speed <= 20f && distance <= GameMain.mainPlayer.mecha.buildArea *
                GameMain.mainPlayer.mecha.buildArea) ready.Add(target);
        }
        foreach (var target in ready)
        {
            declinedLocally.Remove(target);
            Multiplayer.Session.Network.SendPacket(new BuildTargetReadyPacket(target.PlanetId, target.PrebuildId));
        }
    }

    private void SeedFactory(PlanetFactory factory)
    {
        for (var i = 1; i < factory.prebuildCursor; i++)
        {
            ref var prebuild = ref factory.prebuildPool[i];
            if (prebuild.id != i || prebuild.itemRequired != 0 || prebuild.isDestroyed ||
                claims.TryGet(factory.planetId, i, out _)) continue;
            if (prebuild.builderLaunched && TryFindLaunchedOwner(factory, i, out var kind, out var ownerId))
            {
                var claim = claims.Assign(factory.planetId, i, kind, ownerId);
                claims.MarkLaunched(factory.planetId, i, claim.Generation, kind, ownerId);
            }
            else
            {
                prebuild.builderLaunched = false;
                prebuild.builderModuleId = 0;
                prebuild.builderValue = 0f;
                pending.Add((factory.planetId, i));
            }
        }
    }

    private static bool TryFindLaunchedOwner(PlanetFactory factory, int prebuildId,
        out BuildOwnerKind kind, out int ownerId)
    {
        kind = BuildOwnerKind.None;
        ownerId = 0;
        var drones = factory.constructionSystem.drones;
        for (var i = 1; i < drones.cursor; i++)
        {
            ref var drone = ref drones.buffer[i];
            if (drone.id != i || drone.stage == 0 || drone.stage == 4 ||
                drone.targetObjectId != -prebuildId && drone.nextTarget1ObjectId != -prebuildId &&
                drone.nextTarget2ObjectId != -prebuildId && drone.nextTarget3ObjectId != -prebuildId) continue;
            if (drone.owner == 0)
            {
                kind = BuildOwnerKind.Player;
                ownerId = Multiplayer.Session.LocalPlayer.Id;
                return true;
            }
            if (drone.owner >= factory.constructionSystem.constructionModules.cursor) continue;
            var module = factory.constructionSystem.constructionModules.buffer[drone.owner];
            if (module == null || module.id != drone.owner || module.entityId <= 0) continue;
            kind = BuildOwnerKind.Base;
            ownerId = module.entityId;
            return true;
        }
        return false;
    }

    public void SeedForSnapshot(PlanetFactory factory)
    {
        if (Multiplayer.Session.IsServer && factory != null && seededFactories.Add(factory))
        {
            SeedFactory(factory);
            ClearQueuedTargets(factory);
        }
        if (Multiplayer.Session.IsServer && factory != null)
        {
            var tasks = new List<(int PlanetId, int PrebuildId)>(pending);
            foreach (var task in tasks)
                if (task.PlanetId == factory.planetId) TryAssign(factory, task.PrebuildId);
        }
    }

    public void InitializeLoadedFactories()
    {
        if (!Multiplayer.Session.IsServer || GameMain.data == null) return;
        for (var i = 0; i < GameMain.data.factoryCount; i++)
        {
            var factory = GameMain.data.factories[i];
            if (factory == null || !seededFactories.Add(factory)) continue;
            SeedFactory(factory);
            ClearQueuedTargets(factory);
        }
        var tasks = new List<(int PlanetId, int PrebuildId)>(pending);
        foreach (var task in tasks)
        {
            var factory = GameMain.galaxy.PlanetById(task.PlanetId)?.factory;
            if (factory != null) TryAssign(factory, task.PrebuildId);
        }
    }

    private static bool IsReady(PlanetFactory factory, int prebuildId)
    {
        if (factory == null || prebuildId <= 0 || prebuildId >= factory.prebuildCursor) return false;
        ref var prebuild = ref factory.prebuildPool[prebuildId];
        return prebuild.id == prebuildId && prebuild.itemRequired == 0 && !prebuild.isDestroyed &&
               !prebuild.builderLaunched;
    }

    private void TryAssign(PlanetFactory factory, int prebuildId)
    {
        if (!IsReady(factory, prebuildId)) return;
        if (!PickOwner(factory, prebuildId, out var kind, out var ownerId)) return;
        pending.Remove((factory.planetId, prebuildId));
        var claim = claims.Assign(factory.planetId, prebuildId, kind, ownerId);
        if (kind == BuildOwnerKind.Player && ownerId != Multiplayer.Session.LocalPlayer.Id)
            awaitingReply.Add((claim.PlanetId, claim.PrebuildId, claim.Generation));
        Multiplayer.Session.Network.SendPacketToStar(new BuildTargetAssignmentPacket(claim), factory.planet.star.id);
        RefreshLocalQueue(factory, claim);
    }

    private bool PickOwner(PlanetFactory factory, int prebuildId, out BuildOwnerKind kind, out int ownerId)
    {
        var selectedKind = BuildOwnerKind.None;
        var selectedId = 0;
        var pos = factory.prebuildPool[prebuildId].pos;
        var bestScore = 0f;

        void Consider(BuildOwnerKind candidateKind, int candidateId, Vector3 location, float range,
            bool enabled, int droneCount)
        {
            if (candidateKind == BuildOwnerKind.Player &&
                rejectedPlayers.TryGetValue((factory.planetId, prebuildId), out var rejected) &&
                rejected.Contains(candidateId)) return;
            if (!enabled || droneCount <= 0 || CountQueued(factory.planetId, candidateKind, candidateId) >= 120)
                return;
            var distance = (pos - location).sqrMagnitude;
            var score = BuildCandidateScore.Calculate(candidateKind, distance, range);
            if (BuildCandidateScore.Beats(score, candidateKind, candidateId, bestScore, selectedKind, selectedId))
            {
                bestScore = score;
                selectedKind = candidateKind;
                selectedId = candidateId;
            }
        }

        var local = GameMain.mainPlayer;
        if (local?.factory == factory && local.planetId == factory.planetId && !Multiplayer.IsDedicated)
        {
            var module = local.mecha.constructionModule;
            Consider(BuildOwnerKind.Player, Multiplayer.Session.LocalPlayer.Id, local.position, local.mecha.buildArea,
                module.droneEnabled && module.droneConstructEnabled && local.speed <= 20f, module.droneCount);
        }

        // The player who placed the prebuild builds it. Another player or a battle base is only a
        // fallback once that player has no drone, has left the planet, or is outside build range.
        if (Multiplayer.Session.Factories.TryGetPrebuildRequest(factory.planetId, prebuildId, out var placerId) &&
            TrySelectPlacer(factory, pos, placerId, out kind, out ownerId)) return true;

        foreach (var entry in Multiplayer.Session.Server.Players.Connected)
        {
            var player = entry.Value;
            if (player.Data.LocalPlanetId != factory.planetId ||
                !remoteBuilders.TryGetValue(player.Id, out var capability)) continue;
            Consider(BuildOwnerKind.Player, player.Id, player.Data.LocalPlanetPosition.ToVector3(),
                capability.BuildArea, capability.Enabled && capability.CanLaunch, capability.DroneCount);
        }

        var modules = factory.constructionSystem.constructionModules;
        for (var i = 1; i < modules.cursor; i++)
        {
            var module = modules.buffer[i];
            if (module == null || module.id != i || module.entityId <= 0 || module.entityId >= factory.entityCursor)
                continue;
            ref var entity = ref factory.entityPool[module.entityId];
            if (entity.id != module.entityId || module.battleBaseId <= 0 ||
                module.battleBaseId >= factory.defenseSystem.battleBases.cursor) continue;
            var battleBase = factory.defenseSystem.battleBases.buffer[module.battleBaseId];
            if (battleBase == null || battleBase.id != module.battleBaseId ||
                battleBase.energy < Configs.freeMode.droneEjectEnergy) continue;
            Consider(BuildOwnerKind.Base, module.entityId, entity.pos, module.baseBuildRange,
                module.droneEnabled && module.droneConstructEnabled, module.droneCount);
        }
        kind = selectedKind;
        ownerId = selectedId;
        return kind != BuildOwnerKind.None;
    }

    private bool TrySelectPlacer(PlanetFactory factory, Vector3 pos, ushort placerId,
        out BuildOwnerKind kind, out int ownerId)
    {
        kind = BuildOwnerKind.None;
        ownerId = 0;
        var selectedKind = BuildOwnerKind.None;
        var selectedId = 0;
        var score = 0f;
        void Remember(Vector3 location, float range, bool enabled, int droneCount)
        {
            if (!enabled || droneCount <= 0 ||
                CountQueued(factory.planetId, BuildOwnerKind.Player, placerId) >= 120) return;
            var candidate = BuildCandidateScore.Calculate(BuildOwnerKind.Player,
                (pos - location).sqrMagnitude, range);
            if (candidate <= score) return;
            score = candidate;
            selectedKind = BuildOwnerKind.Player;
            selectedId = placerId;
        }

        var local = GameMain.mainPlayer;
        if (local != null && Multiplayer.Session.LocalPlayer.Id == placerId && local.factory == factory &&
            local.planetId == factory.planetId && !Multiplayer.IsDedicated)
        {
            var module = local.mecha.constructionModule;
            Remember(local.position, local.mecha.buildArea,
                module.droneEnabled && module.droneConstructEnabled && local.speed <= 20f, module.droneCount);
        }
        foreach (var entry in Multiplayer.Session.Server.Players.Connected)
        {
            var player = entry.Value;
            if (player.Id != placerId || player.Data.LocalPlanetId != factory.planetId ||
                !remoteBuilders.TryGetValue(player.Id, out var capability)) continue;
            Remember(player.Data.LocalPlanetPosition.ToVector3(), capability.BuildArea,
                capability.Enabled && capability.CanLaunch, capability.DroneCount);
        }
        kind = selectedKind;
        ownerId = selectedId;
        return kind != BuildOwnerKind.None;
    }

    private int CountQueued(int planetId, BuildOwnerKind kind, int ownerId)
    {
        var count = 0;
        foreach (var claim in claims.All)
            if (claim.PlanetId == planetId && !claim.Launched && claim.OwnerKind == kind && claim.OwnerId == ownerId)
                count++;
        return count;
    }

    public void UpdateRemoteBuilder(ushort playerId, float buildArea, int droneCount, bool enabled, bool canLaunch)
    {
        if (!Multiplayer.Session.IsServer) return;
        if (float.IsNaN(buildArea) || float.IsInfinity(buildArea)) buildArea = 0f;
        remoteBuilders[playerId] = new BuilderCapability(Math.Max(0f, Math.Min(buildArea, 1000f)),
            Math.Max(0, Math.Min(droneCount, 256)), enabled, canLaunch);
    }

    public void PlayerLeft(ushort playerId)
    {
        remoteBuilders.Remove(playerId);
        if (!Multiplayer.Session.IsServer) return;
        foreach (var waiting in baseRevocations.Values) waiting.Remove(playerId);
        var released = new List<BuildTargetClaim>();
        foreach (var claim in claims.All)
            if (claim.OwnerKind == BuildOwnerKind.Player && claim.OwnerId == playerId) released.Add(claim);
        foreach (var claim in released) Release(claim, retry: true);
    }

    public void HandleReply(ushort playerId, BuildTargetAssignmentReplyPacket packet)
    {
        if (!Multiplayer.Session.IsServer || !claims.TryGet(packet.PlanetId, packet.PrebuildId, out var claim) ||
            claim.Generation != packet.Generation || claim.OwnerKind != BuildOwnerKind.Player ||
            claim.OwnerId != playerId) return;
        awaitingReply.Remove((claim.PlanetId, claim.PrebuildId, claim.Generation));
        if (!packet.Accepted)
        {
            if (!rejectedPlayers.TryGetValue((claim.PlanetId, claim.PrebuildId), out var rejected))
                rejectedPlayers[(claim.PlanetId, claim.PrebuildId)] = rejected = new HashSet<int>();
            rejected.Add(playerId);
            Release(claim, retry: true);
        }
    }

    public void HandleReadyNotice(ushort playerId, BuildTargetReadyPacket packet)
    {
        if (!Multiplayer.Session.IsServer) return;
        rejectedPlayers.TryGetValue((packet.PlanetId, packet.PrebuildId), out var rejected);
        rejected?.Remove(playerId);
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        if (IsReady(factory, packet.PrebuildId) && !claims.TryGet(packet.PlanetId, packet.PrebuildId, out _))
        {
            pending.Add((packet.PlanetId, packet.PrebuildId));
            TryAssign(factory, packet.PrebuildId);
        }
    }

    private void Release(BuildTargetClaim claim, bool retry)
    {
        if (!claims.Remove(claim.PlanetId, claim.PrebuildId, claim.Generation)) return;
        awaitingReply.Remove((claim.PlanetId, claim.PrebuildId, claim.Generation));
        localBaseLaunched.Remove((claim.PlanetId, claim.PrebuildId, claim.Generation));
        var wasRevoking = baseRevocations.Remove((claim.PlanetId, claim.PrebuildId, claim.Generation));
        baseLowEnergySince.Remove((claim.PlanetId, claim.PrebuildId, claim.Generation));
        var factory = GameMain.galaxy.PlanetById(claim.PlanetId)?.factory;
        if (factory != null)
        {
            ResetLocalPrebuildClaim(factory, claim.PrebuildId);
            if (retry && claim.OwnerKind == BuildOwnerKind.Player && claim.Launched &&
                claim.OwnerId != Multiplayer.Session.LocalPlayer.Id)
                Multiplayer.Session.Drones.CancelRemoteBuildTarget(factory, (ushort)claim.OwnerId,
                    claim.PrebuildId);
            if (!wasRevoking)
                Multiplayer.Session.Network.SendPacketToStar(new BuildTargetAssignmentPacket(claim,
                        removed: true, canceled: retry), factory.planet.star.id);
            RefreshLocalQueue(factory, claim);
            if (retry && IsReady(factory, claim.PrebuildId))
                pending.Add((claim.PlanetId, claim.PrebuildId));
        }
    }

    public void TargetRemoved(PlanetFactory factory, int prebuildId)
    {
        pending.Remove((factory.planetId, prebuildId));
        rejectedPlayers.Remove((factory.planetId, prebuildId));
        declinedLocally.Remove((factory.planetId, prebuildId));
        localBaseLaunched.RemoveWhere(key => key.PlanetId == factory.planetId && key.PrebuildId == prebuildId);
        renderedRemoteLaunches.RemoveWhere(key => key.PlanetId == factory.planetId && key.PrebuildId == prebuildId);
        releaseSent.RemoveWhere(key => key.PlanetId == factory.planetId && key.PrebuildId == prebuildId);
        pendingBaseAcks.RemoveWhere(key => key.PlanetId == factory.planetId && key.PrebuildId == prebuildId);
        var revoked = new List<(int PlanetId, int PrebuildId, long Generation)>();
        foreach (var key in baseRevocations.Keys)
            if (key.PlanetId == factory.planetId && key.PrebuildId == prebuildId) revoked.Add(key);
        foreach (var key in revoked) baseRevocations.Remove(key);
        revoked.Clear();
        foreach (var key in baseLowEnergySince.Keys)
            if (key.PlanetId == factory.planetId && key.PrebuildId == prebuildId) revoked.Add(key);
        foreach (var key in revoked) baseLowEnergySince.Remove(key);
        if (claims.TryGet(factory.planetId, prebuildId, out var claim))
        {
            if (Multiplayer.Session.IsServer) Release(claim, retry: false);
            else claims.Remove(claim.PlanetId, claim.PrebuildId, claim.Generation);
        }
    }

    public void ReleaseLocalTarget(PlanetFactory factory, int prebuildId, ConstructionModuleComponent module)
    {
        if (factory == null || prebuildId <= 0 || !claims.TryGet(factory.planetId, prebuildId, out var claim))
            return;
        var owns = module.entityId == 0
            ? claim.OwnerKind == BuildOwnerKind.Player && claim.OwnerId == Multiplayer.Session.LocalPlayer.Id
            : claim.OwnerKind == BuildOwnerKind.Base && claim.OwnerId == module.entityId;
        if (!owns) return;
        if (Multiplayer.Session.IsServer)
        {
            if (claim.OwnerKind == BuildOwnerKind.Base) BeginBaseRevocation(claim);
            else Release(claim, retry: true);
        }
        else if (claim.OwnerKind == BuildOwnerKind.Player)
            Multiplayer.Session.Network.SendPacket(new BuildTargetAssignmentReplyPacket(
                claim.PlanetId, claim.PrebuildId, claim.Generation, accepted: false));
    }

    public void ReleaseLocalPlayerTargets(int planetId)
    {
        var localId = Multiplayer.Session.LocalPlayer.Id;
        var released = new List<BuildTargetClaim>();
        foreach (var claim in claims.All)
            if (claim.PlanetId == planetId && claim.OwnerKind == BuildOwnerKind.Player &&
                claim.OwnerId == localId && !claim.Launched) released.Add(claim);
        foreach (var claim in released)
        {
            if (Multiplayer.Session.IsServer) Release(claim, retry: true);
            else Multiplayer.Session.Network.SendPacket(new BuildTargetAssignmentReplyPacket(
                claim.PlanetId, claim.PrebuildId, claim.Generation, accepted: false));
        }
        var module = GameMain.mainPlayer?.mecha?.constructionModule;
        if (module?.buildTargets != null)
        {
            Array.Clear(module.buildTargets, 0, module.buildTargets.Length);
            module.buildTargetTotalCount = 0;
        }
    }

    public void AbortLocalConstructionDrones(PlanetFactory factory)
    {
        if (factory == null) return;
        var drones = factory.constructionSystem.drones;
        for (var i = 1; i < drones.cursor; i++)
        {
            ref var drone = ref drones.buffer[i];
            if (drone.id != i || drone.owner != 0 || drone.stage == 0 || drone.stage == 4 ||
                drone.targetObjectId >= 0 && drone.nextTarget1ObjectId >= 0 &&
                drone.nextTarget2ObjectId >= 0 && drone.nextTarget3ObjectId >= 0) continue;
            factory.constructionSystem.ResetDroneTargets(ref drone);
            drone.stage = 4;
            drone.movement = 0;
        }
    }

    public bool TryCreateMechaLaunch(PlanetFactory factory, int target, int next1, int next2, int next3,
        int priority, out BuildDroneLaunchPacket packet)
    {
        packet = null;
        var playerId = Multiplayer.Session.LocalPlayer.Id;
        if (factory == null || target >= 0 ||
            !TryGetLaunchGeneration(factory.planetId, -target, BuildOwnerKind.Player, playerId, out var gen0) ||
            !TryGetOptionalGeneration(factory.planetId, next1, playerId, out var gen1) ||
            !TryGetOptionalGeneration(factory.planetId, next2, playerId, out var gen2) ||
            !TryGetOptionalGeneration(factory.planetId, next3, playerId, out var gen3)) return false;
        packet = new BuildDroneLaunchPacket(playerId, factory.planetId, target, next1, next2, next3,
            gen0, gen1, gen2, gen3, priority);
        return true;
    }

    private bool TryGetOptionalGeneration(int planetId, int targetId, int playerId, out long generation)
    {
        generation = 0;
        return targetId == 0 || targetId < 0 &&
            TryGetLaunchGeneration(planetId, -targetId, BuildOwnerKind.Player, playerId, out generation);
    }

    private bool TryGetLaunchGeneration(int planetId, int prebuildId, BuildOwnerKind kind, int ownerId,
        out long generation)
    {
        generation = 0;
        if (!claims.TryGet(planetId, prebuildId, out var claim) || claim.OwnerKind != kind ||
            claim.OwnerId != ownerId || claim.Launched) return false;
        generation = claim.Generation;
        return true;
    }

    public bool ValidateAndRecordMechaLaunch(BuildDroneLaunchPacket packet, ushort senderId)
    {
        if (!Multiplayer.Session.IsServer || packet == null || packet.PlayerId != senderId ||
            packet.TargetObjectId >= 0) return false;
        var ids = new[] { packet.TargetObjectId, packet.Next1ObjectId, packet.Next2ObjectId, packet.Next3ObjectId };
        var generations = new[] { packet.TargetGeneration, packet.Next1Generation, packet.Next2Generation,
            packet.Next3Generation };
        if (!claims.ValidateLaunch(packet.PlanetId, ids, generations, BuildOwnerKind.Player, senderId,
                requireUnlaunched: true)) return false;
        for (var i = 0; i < ids.Length; i++)
            if (ids[i] < 0 && awaitingReply.Contains((packet.PlanetId, -ids[i], generations[i]))) return false;
        for (var i = 0; i < ids.Length; i++)
            if (ids[i] < 0) MarkLaunched(packet.PlanetId, -ids[i], generations[i], BuildOwnerKind.Player, senderId);
        return true;
    }

    public void LocalMechaLaunched(BuildDroneLaunchPacket packet)
    {
        if (Multiplayer.Session.IsServer)
        {
            if (ValidateAndRecordMechaLaunch(packet, Multiplayer.Session.LocalPlayer.Id))
                Multiplayer.Session.Server.SendPacketToStar(packet,
                    GameMain.galaxy.PlanetById(packet.PlanetId).star.id);
        }
        else
        {
            ReceiveValidatedLaunch(packet);
            Multiplayer.Session.Network.SendPacket(packet);
        }
    }

    public void RecordBaseLaunch(PlanetFactory factory, int baseEntityId, int target, int next1, int next2,
        int next3)
    {
        if (factory == null) return;
        var ids = new[] { target, next1, next2, next3 };
        foreach (var id in ids)
        {
            if (id >= 0 || !claims.TryGet(factory.planetId, -id, out var claim) ||
                claim.OwnerKind != BuildOwnerKind.Base || claim.OwnerId != baseEntityId) continue;
            localBaseLaunched.Add((factory.planetId, -id, claim.Generation));
            MarkLaunched(factory.planetId, -id, claim.Generation, BuildOwnerKind.Base, baseEntityId);
        }
    }

    private void MarkLaunched(int planetId, int prebuildId, long generation, BuildOwnerKind kind, int ownerId)
    {
        if (!claims.MarkLaunched(planetId, prebuildId, generation, kind, ownerId)) return;
        var factory = GameMain.galaxy.PlanetById(planetId)?.factory;
        if (factory != null && prebuildId < factory.prebuildCursor && factory.prebuildPool[prebuildId].id == prebuildId)
            factory.prebuildPool[prebuildId].builderLaunched = true;
        if (Multiplayer.Session.IsServer && claims.TryGet(planetId, prebuildId, out var claim) && factory != null)
            Multiplayer.Session.Network.SendPacketToStar(new BuildTargetAssignmentPacket(claim), factory.planet.star.id);
    }

    public bool ReceiveValidatedLaunch(BuildDroneLaunchPacket packet)
    {
        var ids = new[] { packet.TargetObjectId, packet.Next1ObjectId, packet.Next2ObjectId, packet.Next3ObjectId };
        var generations = new[] { packet.TargetGeneration, packet.Next1Generation, packet.Next2Generation,
            packet.Next3Generation };
        if (!claims.ValidateLaunch(packet.PlanetId, ids, generations, BuildOwnerKind.Player,
                packet.PlayerId, requireUnlaunched: false)) return false;
        for (var i = 0; i < ids.Length; i++)
            if (ids[i] < 0) MarkLaunched(packet.PlanetId, -ids[i], generations[i], BuildOwnerKind.Player, packet.PlayerId);
        return true;
    }

    public bool TryRenderRemoteLaunch(BuildDroneLaunchPacket packet) =>
        renderedRemoteLaunches.Add((packet.PlanetId, -packet.TargetObjectId, packet.TargetGeneration));

    public void ReceiveAssignment(BuildTargetAssignmentPacket packet)
    {
        if (Multiplayer.Session.IsServer || packet.PrebuildId <= 0 || packet.Generation <= 0) return;
        var old = claims.TryGet(packet.PlanetId, packet.PrebuildId, out var previous) ? previous : default;
        if (packet.Removed)
        {
            if (packet.OwnerKind == BuildOwnerKind.Base)
                pendingBaseAcks.Add((packet.PlanetId, packet.PrebuildId, packet.Generation));
            if (packet.Canceled && old.OwnerKind == BuildOwnerKind.Player && old.Launched &&
                old.OwnerId != Multiplayer.Session.LocalPlayer.Id)
                Multiplayer.Session.Drones.CancelRemoteBuildTarget(
                    GameMain.galaxy.PlanetById(packet.PlanetId)?.factory, (ushort)old.OwnerId,
                    packet.PrebuildId);
            if (claims.Remove(packet.PlanetId, packet.PrebuildId, packet.Generation))
            {
                var removedFactory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
                ResetLocalPrebuildClaim(removedFactory, packet.PrebuildId);
                RefreshLocalQueue(removedFactory, old);
            }
            return;
        }
        var claim = packet.ToClaim();
        if (!claims.Apply(claim)) return;
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        if (factory == null) return;
        if (old.Generation > 0) RefreshLocalQueue(factory, old);
        RefreshLocalQueue(factory, claim);
        if (claim.OwnerKind == BuildOwnerKind.Player && claim.OwnerId == Multiplayer.Session.LocalPlayer.Id &&
            !claim.Launched)
        {
            var accepted = CanQueue(factory, GameMain.mainPlayer.mecha.constructionModule, claim.PrebuildId);
            Multiplayer.Session.Network.SendPacket(new BuildTargetAssignmentReplyPacket(
                claim.PlanetId, claim.PrebuildId, claim.Generation, accepted));
            if (!accepted)
            {
                declinedLocally.Add((claim.PlanetId, claim.PrebuildId));
                claims.Remove(claim.PlanetId, claim.PrebuildId, claim.Generation);
                RefreshLocalQueue(factory, claim);
            }
        }
    }

    private void RefreshLocalQueue(PlanetFactory factory, BuildTargetClaim claim)
    {
        if (factory == null || claim.PrebuildId <= 0) return;
        ConstructionModuleComponent module = null;
        if (claim.OwnerKind == BuildOwnerKind.Player && claim.OwnerId == Multiplayer.Session.LocalPlayer.Id &&
            GameMain.mainPlayer.factory == factory)
            module = GameMain.mainPlayer.mecha.constructionModule;
        else if (claim.OwnerKind == BuildOwnerKind.Base && claim.OwnerId > 0 &&
                 claim.OwnerId < factory.entityCursor && factory.entityPool[claim.OwnerId].id == claim.OwnerId)
        {
            var moduleId = factory.entityPool[claim.OwnerId].constructionModuleId;
            if (moduleId > 0 && moduleId < factory.constructionSystem.constructionModules.cursor)
                module = factory.constructionSystem.constructionModules.buffer[moduleId];
        }
        if (module == null) return;
        module.SearchBuildTargets(factory, GameMain.mainPlayer, true);
    }

    private static void ResetLocalPrebuildClaim(PlanetFactory factory, int prebuildId)
    {
        if (factory == null || prebuildId <= 0 || prebuildId >= factory.prebuildCursor ||
            factory.prebuildPool[prebuildId].id != prebuildId) return;
        ref var prebuild = ref factory.prebuildPool[prebuildId];
        prebuild.builderLaunched = false;
        prebuild.builderModuleId = 0;
        prebuild.builderValue = 0f;
    }

    public byte[] ExportSnapshot(int planetId)
    {
        var entries = new List<BuildTargetClaim>();
        foreach (var claim in claims.All)
            if (claim.PlanetId == planetId &&
                !baseRevocations.ContainsKey((claim.PlanetId, claim.PrebuildId, claim.Generation)))
                entries.Add(claim);
        return BuildTargetClaimSnapshot.Export(entries, planetId);
    }

    public void ImportSnapshot(PlanetFactory factory, byte[] data)
    {
        if (factory == null || data == null) return;
        var imported = BuildTargetClaimSnapshot.Import(data, factory.planetId, factory.prebuildCursor);
        claims.ClearPlanet(factory.planetId);
        ClearQueuedTargets(factory);
        foreach (var claim in imported)
        {
            var id = claim.PrebuildId;
            if (id > 0 && id < factory.prebuildCursor && factory.prebuildPool[id].id == id)
                claims.Apply(claim);
        }
        foreach (var claim in claims.All)
            if (claim.PlanetId == factory.planetId && !claim.Launched) RefreshLocalQueue(factory, claim);
    }

    public void RefreshFactoryQueues(PlanetFactory factory)
    {
        if (factory == null) return;
        foreach (var claim in claims.All)
            if (claim.PlanetId == factory.planetId && !claim.Launched) RefreshLocalQueue(factory, claim);
    }

    private static void ClearQueuedTargets(PlanetFactory factory)
    {
        var modules = factory.constructionSystem.constructionModules;
        for (var i = 1; i < modules.cursor; i++)
        {
            var module = modules.buffer[i];
            if (module?.buildTargets == null) continue;
            Array.Clear(module.buildTargets, 0, module.buildTargets.Length);
            module.buildTargetTotalCount = 0;
        }
        if (GameMain.mainPlayer.factory == factory)
        {
            var module = GameMain.mainPlayer.mecha.constructionModule;
            if (module.buildTargets != null) Array.Clear(module.buildTargets, 0, module.buildTargets.Length);
            module.buildTargetTotalCount = 0;
        }
    }

    private readonly struct BuilderCapability
    {
        public BuilderCapability(float buildArea, int droneCount, bool enabled, bool canLaunch)
        {
            BuildArea = buildArea;
            DroneCount = droneCount;
            Enabled = enabled;
            CanLaunch = canLaunch;
        }
        public float BuildArea { get; }
        public int DroneCount { get; }
        public bool Enabled { get; }
        public bool CanLaunch { get; }
    }
}
