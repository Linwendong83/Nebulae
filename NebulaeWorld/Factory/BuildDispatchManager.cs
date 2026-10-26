using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.DataStructures;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Factory;

namespace NebulaeWorld.Factory;

/// <summary>Native queues remain shared. Ownership begins at flight dispatch, never at discovery.</summary>
internal sealed partial class BuildDispatchManager : IDisposable
{
    public PrebuildInstances Instances { get; } = new();
    private readonly BuildTargetClaims claims = new();
    private readonly HashSet<PlanetFactory> seededFactories = new();
    private readonly Dictionary<ushort, BuilderCapability> remoteBuilders = new();
    private readonly Dictionary<(ushort Player, int Planet), (ConstructionLaunchOfferPacket Packet, long Tick)> offers = new();
    private readonly Dictionary<(ushort Player, int Planet), long> offerSequences = new();
    private readonly Dictionary<(int Planet, int Target, long Generation), long> awaitingLaunch = new();
    private readonly HashSet<(int Planet, int Target, long Generation)> revoking = new();
    private readonly HashSet<(int Planet, int Target, long Generation)> renderedRemoteLaunches = new();
    private readonly Dictionary<int, (int Head, long Tick)> lastOfferTick = new();
    private long nextOffer;
    private readonly DiagnosticWatchdog diagnosticWaits = new();

    public void Dispose() { OnFactoriesUnloaded(); remoteBuilders.Clear(); pendingSupply.Clear(); supplySequences.Clear(); GC.SuppressFinalize(this); }
    public void OnFactoriesUnloaded()
    {
        Instances.Clear(); claims.Clear(); seededFactories.Clear(); offers.Clear(); offerSequences.Clear();
        awaitingLaunch.Clear(); revoking.Clear(); renderedRemoteLaunches.Clear(); lastOfferTick.Clear();
        // Offer/supply sequence numbers live for the connection, across factory unload/reload.
        // Material receipts must also settle after the player leaves the supplying planet.
        ClearBaseViews();
        suppliedStores.Clear(); storeTicks.Clear(); pendingStores.Clear();
        diagnosticWaits.Clear();
    }
    public bool TryGet(int planetId, int prebuildId, out BuildTargetClaim claim) => claims.TryGet(planetId, prebuildId, out claim);
    public static PlanetFactory FindFactory(PrebuildData[] pool)
    {
        var data = GameMain.data;
        if (data == null || pool == null) return null;
        for (var i = 0; i < data.factoryCount; i++)
            if (data.factories[i] != null && ReferenceEquals(data.factories[i].prebuildPool, pool)) return data.factories[i];
        return null;
    }
    public bool CanQueue(PlanetFactory factory, ConstructionModuleComponent module, int id)
    {
        if (!IsReady(factory, id) || module == null || !module.droneEnabled || !module.droneConstructEnabled || module.droneCount <= 0 ||
            claims.TryGet(factory.planetId, id, out _)) return false;
        if (module.entityId > 0) return Multiplayer.Session.IsServer;
        var player = GameMain.mainPlayer;
        return player?.isAlive == true && player.factory == factory && player.speed <= 20f &&
            (factory.prebuildPool[id].pos - player.position).sqrMagnitude <= player.mecha.buildArea * player.mecha.buildArea;
    }
    private static bool IsReady(PlanetFactory factory, int id) => factory != null && id > 0 && id < factory.prebuildCursor &&
        factory.prebuildPool[id].id == id && factory.prebuildPool[id].itemRequired == 0 &&
        !factory.prebuildPool[id].isDestroyed && !factory.prebuildPool[id].builderLaunched;

    public void BeginLaunchFrame(PlanetFactory factory)
    {
        for (var id = 1; id < factory.prebuildCursor; id++)
        {
            ref var site = ref factory.prebuildPool[id];
            if (site.id != id || site.builderLaunched) continue;
            site.builderModuleId = 0; site.builderValue = 0f;
        }
    }
    public void OfferLocalFlight(PlanetFactory factory, ConstructionModuleComponent module)
    {
        if (Multiplayer.Session.IsServer || module.entityId != 0 || module.droneIdleCount <= 0 || GameMain.mainPlayer.mecha.coreEnergy <= 0 ||
            module.buildTargets == null || module.buildTargets[0].objectId <= 0) return;
        var head = module.buildTargets[0].objectId;
        // PreLaunchDrone has already compared native construction and repair priorities.
        if (factory.prebuildPool[head].builderModuleId != -1) return;
        if (lastOfferTick.TryGetValue(factory.planetId, out var last) && last.Head == head && GameMain.gameTick - last.Tick < 10) return;
        var ids = new int[4]; var instances = new long[4];
        for (var i = 0; i < 4; i++)
        {
            var id = module.buildTargets[i].objectId;
            if (id <= 0 || !CanQueue(factory, module, id)) continue;
            ids[i] = -id; instances[i] = Instances.Get(factory, id);
        }
        if (ids[0] == 0 || instances[0] <= 0) return;
        lastOfferTick[factory.planetId] = (head, GameMain.gameTick);
        Multiplayer.Session.Network.SendPacket(new ConstructionLaunchOfferPacket
        { PlanetId = factory.planetId, Sequence = ++nextOffer, Targets = ids, Instances = instances, Priority = module.dronePriority });
    }
    public void ReceiveOffer(ushort player, ConstructionLaunchOfferPacket packet)
    {
        if (!Multiplayer.Session.IsServer || !ConstructionFlight.ValidTargets(packet.Targets, packet.Instances) || packet.Sequence <= 0) return;
        var key = (player, packet.PlanetId);
        if (offerSequences.TryGetValue(key, out var previous) && packet.Sequence <= previous) return;
        offerSequences[key] = packet.Sequence; offers[key] = (packet, GameMain.gameTick);
    }
    public void PrepareLaunches(PlanetFactory factory)
    {
        if (!Multiplayer.Session.IsServer) { BeginLaunchFrame(factory); return; }
        var candidates = new List<(ushort Player, ConstructionLaunchOfferPacket Offer, float Score)>();
        foreach (var entry in offers.ToArray())
        {
            if (entry.Key.Planet != factory.planetId) continue;
            offers.Remove(entry.Key);
            var player = Multiplayer.Session.Server.Players.Get(entry.Key.Player);
            if (player?.Data.LocalPlanetId != factory.planetId || ConstructionDroneLife.IsDead(entry.Key.Player) || GameMain.gameTick - entry.Value.Tick > 60 ||
                !remoteBuilders.TryGetValue(entry.Key.Player, out var capability) || !capability.Enabled || !capability.CanLaunch ||
                capability.Count <= 0 || GameMain.gameTick - capability.Tick > 120 ||
                claims.All.Any(c => c.OwnerKind == BuildOwnerKind.Player && c.OwnerId == entry.Key.Player && !c.Launched)) continue;
            var offer = entry.Value.Packet; var head = -offer.Targets[0];
            if (!IsReady(factory, head) || Instances.Get(factory, head) != offer.Instances[0] || claims.TryGet(factory.planetId, head, out _)) continue;
            var distance = (factory.prebuildPool[head].pos - player.Data.LocalPlanetPosition.ToVector3()).sqrMagnitude;
            var score = BuildCandidateScore.Calculate(BuildOwnerKind.Player, distance, capability.Range);
            if (score > 0f) candidates.Add((entry.Key.Player, offer, score));
        }
        foreach (var candidate in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Player))
        {
            var offer = candidate.Offer; var head = -offer.Targets[0];
            if (!IsReady(factory, head) || claims.TryGet(factory.planetId, head, out _)) continue;
            ref var site = ref factory.prebuildPool[head];
            if (site.builderModuleId != 0 && site.builderValue >= candidate.Score) continue;
            var ids = (int[])offer.Targets.Clone();
            for (var i = 1; i < 4; i++)
                if (ids[i] != 0 && (i >= factory.gameData.history.constructionDroneMovement ||
                    !IsReady(factory, -ids[i]) || Instances.Get(factory, -ids[i]) != offer.Instances[i] ||
                    claims.TryGet(factory.planetId, -ids[i], out _))) ids[i] = 0;
            var generations = new long[4];
            for (var i = 0; i < 4; i++)
            {
                if (ids[i] == 0) continue;
                var id = -ids[i]; var claim = claims.Assign(factory.planetId, id, BuildOwnerKind.Player, candidate.Player);
                generations[i] = claim.Generation; awaitingLaunch[(factory.planetId, id, claim.Generation)] = GameMain.gameTick;
                factory.prebuildPool[id].builderLaunched = true;
                Multiplayer.Session.Network.SendPacketToStar(new BuildTargetAssignmentPacket(claim), factory.planet.star.id);
                factory.constructionSystem.RemoveBuildTargetFromModules(id);
            }
            var grant = new ConstructionLaunchGrantPacket();
            Fill(grant, candidate.Player, factory.planetId, ids, generations, offer.Priority);
            Multiplayer.Session.Server.Players.Get(candidate.Player).Connection.SendPacket(grant);
        }
    }
    internal static void Fill(BuildDroneLaunchPacket p, ushort player, int planet, int[] ids, long[] generations, int priority)
    {
        p.PlayerId = player; p.PlanetId = planet; p.TargetObjectId = ids[0];
        p.Next1ObjectId = ids[1]; p.Next2ObjectId = ids[2]; p.Next3ObjectId = ids[3];
        p.TargetGeneration = generations[0]; p.Next1Generation = generations[1];
        p.Next2Generation = generations[2]; p.Next3Generation = generations[3]; p.DronePriority = priority;
    }
    private static int[] Targets(BuildDroneLaunchPacket p) => new[] { p.TargetObjectId, p.Next1ObjectId, p.Next2ObjectId, p.Next3ObjectId };
    private static long[] Generations(BuildDroneLaunchPacket p) => new[] { p.TargetGeneration, p.Next1Generation, p.Next2Generation, p.Next3Generation };
    public void ReceiveGrant(ConstructionLaunchGrantPacket packet)
    {
        if (Multiplayer.Session.IsServer || packet.PlayerId != Multiplayer.Session.LocalPlayer.Id) return;
        var ids = Targets(packet); var generations = Generations(packet);
        if (!claims.ValidateLaunch(packet.PlanetId, ids, generations, BuildOwnerKind.Player, packet.PlayerId, true)) return;
        var player = GameMain.mainPlayer; var factory = player?.factory; var module = player?.mecha.constructionModule;
        var accepted = player?.isAlive == true && factory?.planetId == packet.PlanetId && module != null && module.droneEnabled &&
            module.droneConstructEnabled && module.droneIdleCount > 0 && module.dronePriority == packet.DronePriority && player.speed <= 20f && player.mecha.coreEnergy > 0;
        for (var i = 0; accepted && i < 4; i++)
        {
            if (ids[i] == 0) continue;
            var id = -ids[i];
            accepted = id > 0 && id < factory.prebuildCursor && factory.prebuildPool[id].id == id &&
                factory.prebuildPool[id].itemRequired == 0 && !factory.prebuildPool[id].isDestroyed &&
                (factory.prebuildPool[id].pos - player.position).sqrMagnitude <= player.mecha.buildArea * player.mecha.buildArea;
        }
        if (!accepted)
        {
            for (var i = 0; i < 4; i++) if (ids[i] < 0)
                    Multiplayer.Session.Network.SendPacket(new BuildTargetAssignmentReplyPacket(packet.PlanetId, -ids[i], generations[i], false));
            return;
        }
        module.EjectMechaDrone(factory, player, ids[0], ids[1], ids[2], ids[3]);
        for (var i = 0; i < 4; i++) if (ids[i] < 0) factory.constructionSystem.RemoveBuildTargetFromModules(-ids[i]);
    }
    public void RecordBaseLaunch(PlanetFactory factory, int entity, int target, int next1, int next2, int next3)
    {
        if (!Multiplayer.Session.IsServer) return;
        foreach (var id in new[] { target, next1, next2, next3 })
            if (id < 0) MarkLaunched(claims.Assign(factory.planetId, -id, BuildOwnerKind.Base, entity));
    }
    public bool TryCreateMechaLaunch(PlanetFactory factory, int target, int next1, int next2, int next3, int priority, out BuildDroneLaunchPacket packet)
    {
        packet = null; var ids = new[] { target, next1, next2, next3 }; var generations = new long[4];
        if (factory == null || target >= 0) return false;
        var player = Multiplayer.Session.LocalPlayer.Id;
        for (var i = 0; i < 4; i++)
        {
            if (ids[i] == 0) continue;
            if (ids[i] > 0) return false;
            if (!claims.TryGet(factory.planetId, -ids[i], out var claim))
            {
                if (!Multiplayer.Session.IsServer) return false;
                claim = claims.Assign(factory.planetId, -ids[i], BuildOwnerKind.Player, player);
                Multiplayer.Session.Network.SendPacketToStar(new BuildTargetAssignmentPacket(claim), factory.planet.star.id);
            }
            if (claim.Launched || claim.OwnerKind != BuildOwnerKind.Player || claim.OwnerId != player) return false;
            generations[i] = claim.Generation;
        }
        packet = new BuildDroneLaunchPacket(); Fill(packet, player, factory.planetId, ids, generations, priority); return true;
    }
    public bool ValidateAndRecordMechaLaunch(BuildDroneLaunchPacket packet, ushort sender)
    {
        if (!Multiplayer.Session.IsServer || packet.PlayerId != sender || ConstructionDroneLife.IsDead(sender) ||
            !claims.ValidateLaunch(packet.PlanetId, Targets(packet), Generations(packet), BuildOwnerKind.Player, sender, true)) return false;
        var ids = Targets(packet); var generations = Generations(packet);
        for (var i = 0; i < 4; i++) if (ids[i] < 0 && revoking.Contains((packet.PlanetId, -ids[i], generations[i]))) return false;
        foreach (var id in ids) if (id < 0 && claims.TryGet(packet.PlanetId, -id, out var claim)) MarkLaunched(claim);
        return true;
    }
    public void LocalMechaLaunched(BuildDroneLaunchPacket packet)
    {
        if (Multiplayer.Session.IsServer)
        {
            if (ValidateAndRecordMechaLaunch(packet, packet.PlayerId)) Multiplayer.Session.Server.SendPacketToStar(packet, GameMain.galaxy.PlanetById(packet.PlanetId).star.id);
        }
        else { ReceiveValidatedLaunch(packet); Multiplayer.Session.Network.SendPacket(packet); }
    }
    public bool ReceiveValidatedLaunch(BuildDroneLaunchPacket packet)
    {
        if (!claims.ValidateLaunch(packet.PlanetId, Targets(packet), Generations(packet), BuildOwnerKind.Player, packet.PlayerId, false)) return false;
        foreach (var id in Targets(packet)) if (id < 0 && claims.TryGet(packet.PlanetId, -id, out var claim)) MarkLaunched(claim);
        return true;
    }
    private void MarkLaunched(BuildTargetClaim claim)
    {
        if (!claims.MarkLaunched(claim.PlanetId, claim.PrebuildId, claim.Generation, claim.OwnerKind, claim.OwnerId)) return;
        awaitingLaunch.Remove((claim.PlanetId, claim.PrebuildId, claim.Generation));
        var factory = GameMain.galaxy.PlanetById(claim.PlanetId)?.factory;
        if (factory == null || claim.PrebuildId >= factory.prebuildCursor || factory.prebuildPool[claim.PrebuildId].id != claim.PrebuildId) return;
        factory.prebuildPool[claim.PrebuildId].builderLaunched = true;
        if (Multiplayer.Session.IsServer && claims.TryGet(claim.PlanetId, claim.PrebuildId, out var launched))
            Multiplayer.Session.Network.SendPacketToStar(new BuildTargetAssignmentPacket(launched), factory.planet.star.id);
    }
    public bool TryRenderRemoteLaunch(BuildDroneLaunchPacket packet) => renderedRemoteLaunches.Add((packet.PlanetId, -packet.TargetObjectId, packet.TargetGeneration));
    public void Tick()
    {
        if (!Multiplayer.Session.IsGameLoaded || GameMain.data == null) return;
        if (!Multiplayer.Session.IsServer) { FlushConstructionStorage(); return; }
        InitializeLoadedFactories();
        if (GameMain.gameTick % 30 != 0) return;
        diagnosticWaits.Observe("build-flight", awaitingLaunch.Count != 0 || revoking.Count != 0,
            () => $"[build] awaitingLaunch={awaitingLaunch.Count} revoking={revoking.Count} claims={claims.All.Count()} tick={GameMain.gameTick}",
            60, progress: awaitingLaunch.Count > 0 ? awaitingLaunch.First().Key.GetHashCode() : revoking.Count > 0 ? revoking.First().GetHashCode() : 0);
        foreach (var entry in awaitingLaunch.ToArray())
            if (GameMain.gameTick - entry.Value >= 120 && claims.TryGet(entry.Key.Planet, entry.Key.Target, out var claim) && claim.Generation == entry.Key.Generation)
                BeginRevocation(claim);
        // A dismantled/destroyed base may have lost its module before native recycling ran.
        var activeByPlanet = new Dictionary<int, HashSet<(int Entity, int Target)>>();
        foreach (var claim in claims.All.ToArray())
        {
            if (claim.OwnerKind != BuildOwnerKind.Base) continue;
            var factory = GameMain.galaxy.PlanetById(claim.PlanetId)?.factory;
            if (factory == null) continue;
            if (!activeByPlanet.TryGetValue(claim.PlanetId, out var active))
            {
                activeByPlanet[claim.PlanetId] = active = new HashSet<(int Entity, int Target)>();
                var drones = factory.constructionSystem.drones;
                for (var id = 1; id < drones.cursor; id++)
                {
                    ref var drone = ref drones.buffer[id];
                    if (drone.id != id || drone.owner <= 0 || drone.owner >= factory.constructionSystem.constructionModules.cursor || drone.stage == 0 || drone.stage == 4) continue;
                    var module = factory.constructionSystem.constructionModules.buffer[drone.owner];
                    if (module?.id != drone.owner) continue;
                    foreach (var target in new[] { drone.targetObjectId, drone.nextTarget1ObjectId, drone.nextTarget2ObjectId, drone.nextTarget3ObjectId })
                        if (target < 0) active.Add((module.entityId, -target));
                }
            }
            if (!active.Contains((claim.OwnerId, claim.PrebuildId))) Release(claim, true);
        }
    }
    private void BeginRevocation(BuildTargetClaim claim)
    {
        if (!revoking.Add((claim.PlanetId, claim.PrebuildId, claim.Generation))) return;
        awaitingLaunch.Remove((claim.PlanetId, claim.PrebuildId, claim.Generation));
        var factory = GameMain.galaxy.PlanetById(claim.PlanetId)?.factory;
        var cancellation = new BuildTargetAssignmentPacket(claim, true, true);
        if (factory != null) Multiplayer.Session.Network.SendPacketToStar(cancellation, factory.planet.star.id);
        var owner = Multiplayer.Session.Server.Players.Get((ushort)claim.OwnerId);
        if (owner != null && (factory == null || owner.Data.LocalStarId != factory.planet.star.id))
            owner.Connection.SendPacket(cancellation);
        Log.Debug($"[build] revoking flight planet={claim.PlanetId} target={claim.PrebuildId} generation={claim.Generation} owner={claim.OwnerId}");
    }
    public void HandleReply(ushort player, BuildTargetAssignmentReplyPacket packet)
    {
        if (!packet.Accepted && claims.TryGet(packet.PlanetId, packet.PrebuildId, out var claim) && claim.OwnerKind == BuildOwnerKind.Player &&
            claim.OwnerId == player && claim.Generation == packet.Generation) BeginRevocation(claim);
    }
    public void HandleBaseReleaseAck(ushort player, BuildTargetBaseReleaseAckPacket packet)
    {
        if (!claims.TryGet(packet.PlanetId, packet.PrebuildId, out var claim) || claim.OwnerKind != BuildOwnerKind.Player ||
            claim.OwnerId != player || claim.Generation != packet.Generation || !revoking.Contains((claim.PlanetId, claim.PrebuildId, claim.Generation))) return;
        Release(claim, true);
    }
    private void Release(BuildTargetClaim claim, bool canceled)
    {
        if (!claims.Remove(claim.PlanetId, claim.PrebuildId, claim.Generation)) return;
        awaitingLaunch.Remove((claim.PlanetId, claim.PrebuildId, claim.Generation)); revoking.Remove((claim.PlanetId, claim.PrebuildId, claim.Generation));
        var factory = GameMain.galaxy.PlanetById(claim.PlanetId)?.factory;
        if (factory == null) return;
        ResetSite(factory, claim.PrebuildId);
        if (canceled && Multiplayer.Session.IsServer && claim.OwnerKind == BuildOwnerKind.Player && claim.OwnerId != Multiplayer.Session.LocalPlayer.Id)
            Multiplayer.Session.Drones.CancelRemoteBuildTarget(factory, (ushort)claim.OwnerId, claim.PrebuildId);
        if (Multiplayer.Session.IsServer) Multiplayer.Session.Network.SendPacketToStar(new BuildTargetAssignmentPacket(claim, true, canceled), factory.planet.star.id);
        if (IsReady(factory, claim.PrebuildId)) factory.constructionSystem.AddBuildTargetToModules(claim.PrebuildId, ref factory.prebuildPool[claim.PrebuildId].pos);
    }
    public void ReceiveAssignment(BuildTargetAssignmentPacket packet)
    {
        if (Multiplayer.Session.IsServer || packet.PrebuildId <= 0 || packet.Generation <= 0) return;
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        if (packet.Removed)
        {
            var removed = claims.Remove(packet.PlanetId, packet.PrebuildId, packet.Generation);
            if (packet.Canceled && packet.OwnerKind == BuildOwnerKind.Player && packet.OwnerId == Multiplayer.Session.LocalPlayer.Id)
            {
                if (removed) CancelLocalFlight(factory, packet.PrebuildId);
                Multiplayer.Session.Network.SendPacket(new BuildTargetBaseReleaseAckPacket(packet.PlanetId, packet.PrebuildId, packet.Generation));
            }
            else if (packet.Canceled && packet.OwnerKind == BuildOwnerKind.Player && removed)
                Multiplayer.Session.Drones.CancelRemoteBuildTarget(factory, (ushort)packet.OwnerId, packet.PrebuildId);
            if (removed) { ResetSite(factory, packet.PrebuildId); RefreshFactoryQueues(factory); }
            return;
        }
        if (!claims.Apply(packet.ToClaim()) || factory == null || packet.PrebuildId >= factory.prebuildCursor || factory.prebuildPool[packet.PrebuildId].id != packet.PrebuildId) return;
        factory.prebuildPool[packet.PrebuildId].builderLaunched = true;
        factory.constructionSystem.RemoveBuildTargetFromModules(packet.PrebuildId);
    }
    private void CancelLocalFlight(PlanetFactory factory, int target)
    {
        if (factory == null) return;
        var drones = factory.constructionSystem.drones;
        for (var id = 1; id < drones.cursor; id++)
        {
            ref var drone = ref drones.buffer[id];
            if (drone.id != id || drone.owner != 0 || !ConstructionFlight.Contains(drone.targetObjectId, drone.nextTarget1ObjectId, drone.nextTarget2ObjectId, drone.nextTarget3ObjectId, -target)) continue;
            factory.constructionSystem.ResetDroneTargets(ref drone); drone.stage = 4; drone.movement = 0;
        }
    }
    private static void ResetSite(PlanetFactory factory, int id)
    {
        if (factory == null || id <= 0 || id >= factory.prebuildCursor || factory.prebuildPool[id].id != id) return;
        ref var site = ref factory.prebuildPool[id]; site.builderLaunched = false; site.builderModuleId = 0; site.builderValue = 0;
    }
    public void ReleaseLocalTarget(PlanetFactory factory, int id, ConstructionModuleComponent module)
    {
        if (factory == null || module == null || !claims.TryGet(factory.planetId, id, out var claim)) return;
        if (claim.OwnerKind == BuildOwnerKind.Base ? module.entityId != claim.OwnerId : module.entityId != 0 || claim.OwnerId != Multiplayer.Session.LocalPlayer.Id) return;
        if (Multiplayer.Session.IsServer) Release(claim, true);
        else Multiplayer.Session.Network.SendPacket(new BuildTargetAssignmentReplyPacket(factory.planetId, id, claim.Generation, false));
    }
    public void ReleaseLocalPlayerTargets(int planet)
    {
        foreach (var claim in claims.All.ToArray())
            if (claim.PlanetId == planet && claim.OwnerKind == BuildOwnerKind.Player && claim.OwnerId == Multiplayer.Session.LocalPlayer.Id)
            {
                if (Multiplayer.Session.IsServer) Release(claim, true);
                else Multiplayer.Session.Network.SendPacket(new BuildTargetAssignmentReplyPacket(planet, claim.PrebuildId, claim.Generation, false));
            }
    }
    public void AbortLocalConstructionDrones(PlanetFactory factory)
    {
        if (factory == null) return;
        var drones = factory.constructionSystem.drones;
        for (var id = 1; id < drones.cursor; id++)
        {
            ref var drone = ref drones.buffer[id];
            if (drone.id != id || drone.owner != 0 || drone.stage == 0 || drone.stage == 4 ||
                drone.targetObjectId >= 0 && drone.nextTarget1ObjectId >= 0 && drone.nextTarget2ObjectId >= 0 && drone.nextTarget3ObjectId >= 0) continue;
            factory.constructionSystem.ResetDroneTargets(ref drone); drone.stage = 4; drone.movement = 0;
        }
    }
    public void TargetRemoved(PlanetFactory factory, int id)
    {
        Instances.Remove(factory.planetId, id);
        if (claims.TryGet(factory.planetId, id, out var claim))
        {
            claims.Remove(factory.planetId, id, claim.Generation);
            awaitingLaunch.Remove((factory.planetId, id, claim.Generation)); revoking.Remove((factory.planetId, id, claim.Generation));
        }
        renderedRemoteLaunches.RemoveWhere(k => k.Planet == factory.planetId && k.Target == id);
    }
    public void UpdateRemoteBuilder(ushort player, float area, int count, bool enabled, bool canLaunch)
    {
        if (Multiplayer.Session.IsServer) remoteBuilders[player] = new BuilderCapability(area, count, enabled, canLaunch, GameMain.gameTick);
    }
    public void PlayerDied(ushort player)
    {
        remoteBuilders.Remove(player);
        foreach (var key in offers.Keys.Where(k => k.Player == player).ToArray()) offers.Remove(key);
        foreach (var claim in claims.All.ToArray())
        {
            if (claim.OwnerKind != BuildOwnerKind.Player || claim.OwnerId != player) continue;
            if (Multiplayer.Session.IsServer) Release(claim, true);
            else if (player == Multiplayer.Session.LocalPlayer.Id)
                Multiplayer.Session.Network.SendPacket(new BuildTargetAssignmentReplyPacket(claim.PlanetId, claim.PrebuildId, claim.Generation, false));
        }
    }
    public void PlayerLeft(ushort player)
    {
        remoteBuilders.Remove(player);
        supplySequences.Remove(player);
        foreach (var key in offerSequences.Keys.Where(k => k.Player == player).ToArray()) { offers.Remove(key); offerSequences.Remove(key); }
        foreach (var claim in claims.All.ToArray()) if (claim.OwnerKind == BuildOwnerKind.Player && claim.OwnerId == player) Release(claim, true);
    }
    public void InitializeLoadedFactories()
    {
        if (!Multiplayer.Session.IsServer || GameMain.data == null) return;
        for (var i = 0; i < GameMain.data.factoryCount; i++) SeedForSnapshot(GameMain.data.factories[i]);
    }
    public void SeedForSnapshot(PlanetFactory factory)
    {
        if (factory == null || !Multiplayer.Session.IsServer || !seededFactories.Add(factory)) return;
        var drones = factory.constructionSystem.drones; var active = new HashSet<int>();
        for (var i = 1; i < drones.cursor; i++)
        {
            ref var drone = ref drones.buffer[i];
            if (drone.id != i || drone.stage == 0 || drone.stage == 4 || drone.owner < 0 || drone.owner >= factory.constructionSystem.constructionModules.cursor) continue;
            var module = drone.owner == 0 ? null : factory.constructionSystem.constructionModules.buffer[drone.owner];
            var kind = module == null ? BuildOwnerKind.Player : BuildOwnerKind.Base;
            var owner = module == null ? Multiplayer.Session.LocalPlayer.Id : module.entityId;
            foreach (var target in new[] { drone.targetObjectId, drone.nextTarget1ObjectId, drone.nextTarget2ObjectId, drone.nextTarget3ObjectId })
                if (target < 0 && -target < factory.prebuildCursor && factory.prebuildPool[-target].id == -target && active.Add(-target))
                {
                    var claim = claims.Assign(factory.planetId, -target, kind, owner);
                    claims.MarkLaunched(factory.planetId, -target, claim.Generation, kind, owner);
                }
        }
        for (var id = 1; id < factory.prebuildCursor; id++) if (factory.prebuildPool[id].id == id && !active.Contains(id)) ResetSite(factory, id);
        RefreshFactoryQueues(factory);
    }
    public byte[] ExportSnapshot(int planet) => Instances.Export(GameMain.galaxy.PlanetById(planet).factory,
        BuildTargetClaimSnapshot.Export(claims.All.Where(c => c.PlanetId == planet), planet));
    public void ImportSnapshot(PlanetFactory factory, byte[] data)
    {
        var imported = BuildTargetClaimSnapshot.Import(Instances.Import(factory, data), factory.planetId, factory.prebuildCursor);
        claims.ClearPlanet(factory.planetId);
        foreach (var claim in imported) if (claim.PrebuildId < factory.prebuildCursor && factory.prebuildPool[claim.PrebuildId].id == claim.PrebuildId) claims.Apply(claim);
        RefreshFactoryQueues(factory);
    }
    public void RefreshFactoryQueues(PlanetFactory factory)
    {
        if (factory == null) return;
        if (Multiplayer.Session.IsServer)
        {
            var modules = factory.constructionSystem.constructionModules;
            for (var id = 1; id < modules.cursor; id++) if (modules.buffer[id]?.id == id) modules.buffer[id].SearchBuildTargets(factory, GameMain.mainPlayer, true);
        }
        if (GameMain.mainPlayer?.factory == factory) GameMain.mainPlayer.mecha.constructionModule.SearchBuildTargets(factory, GameMain.mainPlayer, true);
    }
    private readonly struct BuilderCapability
    {
        public BuilderCapability(float range, int count, bool enabled, bool canLaunch, long tick)
        { Range = float.IsNaN(range) || float.IsInfinity(range) ? 0 : Math.Max(0, Math.Min(range, 1000)); Count = Math.Max(0, Math.Min(count, 256)); Enabled = enabled; CanLaunch = canLaunch; Tick = tick; }
        public readonly float Range;
        public readonly int Count;
        public readonly bool Enabled, CanLaunch;
        public readonly long Tick;
    }
}
