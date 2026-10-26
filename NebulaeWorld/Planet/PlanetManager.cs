#region

using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Planet;
using NebulaeModel.Packets.Session;
using NebulaeModel.Logger;
using NebulaeWorld.Warning;
using UnityEngine;
using HarmonyLib;

#endregion

namespace NebulaeWorld.Planet;

internal class PlanetManager : IDisposable
{
    public readonly ToggleSwitch IsIncomingRequest = new();
    public int TargetPlanet { get; set; }

    public Dictionary<int, byte[]> PendingFactories { get; set; } = new();
    public Dictionary<int, byte[]> PendingTerrainData { get; set; } = new();
    public Dictionary<int, byte[]> PendingBuildAssignments { get; set; } = new();
    private readonly Dictionary<int, byte[]> pendingGenerations = new();
    private readonly HashSet<int> bufferedFactories = new();
    public bool EnableVeinPacket { get; set; } = true;
    public static byte[] PreservedDashboardData { get; set; }
    public FactoryLoadTracker Loads { get; } = new();
    public HostPlanetPreparation HostPreparation { get; }
    public FactoryModelLoader ModelLoader { get; }
    private readonly HashSet<int> terrainLoads = new();
    private readonly HashSet<int> terrainReceived = new();
    private readonly Dictionary<int, byte[]> terrainPayloads = new();
    public HashSet<int> KnownFactoryPlanets { get; } = new();
    public bool BaselineKnown { get; private set; }
    private readonly Dictionary<int, (PlanetData Planet, HashSet<INebulaeConnection> Clients, float Started)> detailRequests = new();
    private byte[] factoryPresence;
    private int factoryPresenceCount = -1;

    public bool IsPristine(PlanetData planet) => Multiplayer.Session.IsInLobby ||
        (BaselineKnown && planet != null && !KnownFactoryPlanets.Contains(planet.id));

    public byte[] ExportFactoryPresence()
    {
        using var writer = new BinaryUtils.Writer();
        writer.BinaryWriter.Write(GameMain.data.factoryCount);
        for (var i = 0; i < GameMain.data.factoryCount; i++) writer.BinaryWriter.Write(GameMain.data.factories[i].planetId);
        return writer.CloseAndGetBytes();
    }
    public void PublishFactoryPresence()
    {
        if (factoryPresenceCount == GameMain.data.factoryCount) return;
        factoryPresenceCount = GameMain.data.factoryCount;
        var bytes = ExportFactoryPresence();
        if (FieldDeltaCodec.Equal(bytes, factoryPresence)) return;
        factoryPresence = bytes;
        Multiplayer.Session.Network.SendPacket(new GlobalGameDataResponse(GlobalGameDataResponse.EDataType.PlanetBaseline, bytes));
    }
    public void ImportFactoryPresence(byte[] bytes)
    {
        using var reader = new BinaryUtils.Reader(bytes);
        var count = reader.BinaryReader.ReadInt32();
        if (count < 0 || count > 1000000) throw new System.IO.InvalidDataException("Invalid factory presence count");
        var ids = new HashSet<int>();
        for (var i = 0; i < count; i++) { var id = reader.BinaryReader.ReadInt32(); if (id <= 0) throw new System.IO.InvalidDataException("Invalid factory planet"); ids.Add(id); }
        var added = ids.Except(KnownFactoryPlanets).ToArray();
        KnownFactoryPlanets.Clear(); KnownFactoryPlanets.UnionWith(ids); BaselineKnown = true;
        if (!Multiplayer.Session.IsGameLoaded) return;
        var refresh = new List<int>();
        foreach (var id in added)
        {
            var planet = GameMain.galaxy.PlanetById(id);
            if (planet == null || !planet.wanted || !planet.loading || terrainLoads.Contains(id)) continue;
            // Another player may have modified a pristine planet while its base was generating.
            BeginTerrainLoad(id); refresh.Add(id);
        }
        if (refresh.Count > 0) Multiplayer.Session.Network.SendPacket(new PlanetDataRequest(refresh.ToArray()));
    }
    public void ReceiveRuntime(PlanetData planet, byte[] bytes)
    {
        if (!AcceptTerrainResponse(planet)) return;
        terrainPayloads[planet.id] = (byte[])bytes.Clone();
    }
    public bool PrepareModeling(PlanetData planet)
    {
        if (!planet.wanted || !terrainLoads.Contains(planet.id)) return true;
        if (!terrainReceived.Contains(planet.id)) return false;
        if (terrainPayloads.TryGetValue(planet.id, out var bytes))
        {
            using var reader = new BinaryUtils.Reader(bytes);
            planet.ImportRuntime(reader.BinaryReader);
            planet.data.modData = planet.modData;
            planet.landPercentDirtyFlag = true;
            terrainPayloads.Remove(planet.id);
        }
        return true;
    }
    public void RequestDetails(PlanetData planet, INebulaeConnection client)
    {
        if (planet.scanned) { SendDetails(planet, client); return; }
        if (detailRequests.TryGetValue(planet.id, out var pending)) { pending.Clients.Add(client); return; }
        detailRequests[planet.id] = (planet, new HashSet<INebulaeConnection> { client }, Time.realtimeSinceStartup);
        PlanetModelingManager.RequestScanPlanet(planet);
    }
    private static void SendDetails(PlanetData planet, INebulaeConnection client) => client.SendPacket(
        new PlanetDetailResponse(planet.id, planet.runtimeVeinGroups ?? Array.Empty<VeinGroup>(), planet.landPercent) { Success = planet.scanned });
    public PlanetManager()
    {
        HostPreparation = new HostPlanetPreparation();
        ModelLoader = new FactoryModelLoader(this);
    }
    public void BeginTerrainLoad(int planet)
    {
        terrainLoads.Add(planet);
        terrainReceived.Remove(planet);
        terrainPayloads.Remove(planet);
    }
    public bool AcceptTerrainResponse(PlanetData planet) => planet != null && planet.wanted && planet.loading &&
        terrainLoads.Contains(planet.id) && terrainReceived.Add(planet.id);
    public void ClearTerrainLoads() { terrainLoads.Clear(); terrainReceived.Clear(); terrainPayloads.Clear(); }
    public void CancelTerrainLoads(StarData star)
    {
        if (star == null) return;
        foreach (var planet in star.planets)
        {
            terrainLoads.Remove(planet.id);
            terrainReceived.Remove(planet.id);
            terrainPayloads.Remove(planet.id);
        }
    }
    public void TerrainLoaded(int planet)
    {
        if (!terrainLoads.Remove(planet)) return;
        terrainReceived.Remove(planet);
        terrainPayloads.Remove(planet);
    }
    private static readonly System.Reflection.FieldInfo currentPlanet = AccessTools.Field(typeof(PlanetModelingManager), "currentFactingPlanet");
    private static readonly System.Reflection.FieldInfo currentStage = AccessTools.Field(typeof(PlanetModelingManager), "currentFactingStage");
    private static readonly System.Reflection.FieldInfo raycastFactory = AccessTools.Field(typeof(RaycastLogic), "factory");
    public static PlanetData LoadingPlanet { get => (PlanetData)currentPlanet.GetValue(null); set => currentPlanet.SetValue(null, value); }
    public static int LoadingStage { get => (int)currentStage.GetValue(null); set => currentStage.SetValue(null, value); }
    private bool ownsPacketPause;
    private PlanetData meshRepairPlanet;
    private float meshRepairAt;

    // Download while terrain is generated. Installing a factory still owns the ordinary
    // packet pause, but merely receiving one must not block the terrain response behind it.
    public void PrefetchFactory(PlanetData planet)
    {
        if (planet == null || planet.factory != null || Loads.Contains(planet.id)) return;
        if (Loads.Begin(planet.id, true))
            Multiplayer.Session.Network.SendPacket(new FactoryLoadRequest(planet.id));
    }

    public void BufferFactory(FactoryData packet)
    {
        PendingFactories[packet.PlanetId] = packet.BinaryData;
        PendingTerrainData[packet.PlanetId] = packet.TerrainModData;
        PendingBuildAssignments[packet.PlanetId] = packet.BuildAssignments;
        pendingGenerations[packet.PlanetId] = packet.EnemyGenerations;
        bufferedFactories.Add(packet.PlanetId);
        TryActivateFactory(GameMain.galaxy.PlanetById(packet.PlanetId));
    }

    public bool TryActivateFactory(PlanetData planet)
    {
        if (planet?.loaded != true || !planet.wanted || !bufferedFactories.Contains(planet.id) ||
            !Loads.TryQueue(planet.id)) return false;
        bufferedFactories.Remove(planet.id);
        Multiplayer.Session.Generations.Import(pendingGenerations[planet.id]);
        pendingGenerations.Remove(planet.id);
        EnqueueFactory(planet);
        return true;
    }

    public void PausePackets(int planet)
    {
        Log.Debug($"[factory-load] pause packets planet={planet}");
        var processor = Multiplayer.Session.Network.PacketProcessor;
        if (!Loads.Paused) ownsPacketPause = processor.EnablePacketProcessing;
        Loads.Pause(planet);
        processor.EnablePacketProcessing = false;
    }

    private void ReleasePackets()
    {
        if (Loads.Paused || !ownsPacketPause) return;
        ownsPacketPause = false;
        var processor = Multiplayer.Session?.Network?.PacketProcessor;
        if (processor != null) processor.EnablePacketProcessing = true;
        Log.Debug("[factory-load] resumed packets");
    }

    public void EnqueueFactory(PlanetData planet)
    {
        planet.factoryLoading = true;
        planet.factingCompletedStage = -1;
        PausePackets(planet.id);
        lock (PlanetModelingManager.fctPlanetReqList)
            if (LoadingPlanet != planet && !PlanetModelingManager.fctPlanetReqList.Contains(planet))
                PlanetModelingManager.fctPlanetReqList.Enqueue(planet);
    }

    public void CompleteFactory(PlanetData planet)
    {
        Log.Debug($"[factory-load] factory completed planet={planet.id} stage={planet.factingCompletedStage}");
        ClearPending(planet.id);
        Loads.Complete(planet.id);
        ReleasePackets();
    }

    public void FailFactory(PlanetData planet, int stage, Exception error)
    {
        ClearPending(planet.id);
        Loads.Cancel(planet.id);
        ReleasePackets();
        planet.factoryLoading = false;
        planet.factoryLoaded = false;
        planet.factingCompletedStage = -1;
        Log.Error($"[factory-load] planet={planet.id} stage={stage} failed", error);
        WarningManager.DisplayCriticalWarning($"Factory loading failed on {planet.displayName} (stage {stage}). See the logfile for details.");
    }

    public void CancelFactory(PlanetData planet)
    {
        if (planet == null) return;
        Loads.Cancel(planet.id);
        ClearPending(planet.id);
        lock (PlanetModelingManager.fctPlanetReqList)
        {
            var count = PlanetModelingManager.fctPlanetReqList.Count;
            for (var i = 0; i < count; i++)
            {
                var queued = PlanetModelingManager.fctPlanetReqList.Dequeue();
                if (queued != planet) PlanetModelingManager.fctPlanetReqList.Enqueue(queued);
            }
        }
        if (LoadingPlanet == planet)
        {
            LoadingPlanet = null;
            LoadingStage = 0;
        }
        planet.factoryLoading = false;
        planet.factingCompletedStage = -1;
        ReleasePackets();
    }

    private void ClearPending(int planet)
    {
        ModelLoader.Cancel(planet);
        PendingFactories.Remove(planet);
        PendingTerrainData.Remove(planet);
        PendingBuildAssignments.Remove(planet);
        pendingGenerations.Remove(planet);
        bufferedFactories.Remove(planet);
    }

    public void PrepareFactory(PlanetData planet)
    {
        if (planet.physics?.raycastLogic != null) raycastFactory.SetValue(planet.physics.raycastLogic, planet.factory);
    }

    public void ApplyTerrain(PlanetData planet)
    {
        if (PendingTerrainData.TryGetValue(planet.id, out var terrain))
        {
            PendingTerrainData.Remove(planet.id);
            var changed = !FieldDeltaCodec.Equal(planet.data.modData, terrain);
            planet.modData = terrain;
            planet.data.modData = terrain;
            if (!changed || planet.type == EPlanetType.Gas) return;
            for (var i = 0; i < planet.dirtyFlags.Length; i++) planet.dirtyFlags[i] = true;
            planet.landPercentDirtyFlag = true;
        }
    }

    public static void RefreshMeshes(PlanetData planet)
    {
        if (planet?.meshColliders == null || planet.meshes == null) return;
        for (var i = 0; i < planet.meshColliders.Length && i < planet.meshes.Length; i++)
            if (planet.meshColliders[i] != null && planet.meshColliders[i].sharedMesh == null && planet.meshes[i] != null)
                planet.meshColliders[i].sharedMesh = planet.meshes[i];
    }

    public void ScheduleMeshRepair(PlanetData planet)
    { meshRepairPlanet = planet; meshRepairAt = Time.realtimeSinceStartup + 1; }

    public void Update()
    {
        HostPreparation.Update();
        if (bufferedFactories.Count > 0)
            foreach (var id in bufferedFactories.ToArray())
            {
                var planet = GameMain.galaxy?.PlanetById(id);
                try { TryActivateFactory(planet); }
                catch (Exception error) { if (planet != null) FailFactory(planet, -1, error); }
            }
        // detailRequests is empty on almost every frame, so skip the snapshot allocation.
        if (detailRequests.Count > 0)
        {
            foreach (var pending in detailRequests.Values.ToArray())
            {
                if (!pending.Planet.scanned && Time.realtimeSinceStartup - pending.Started < 30) continue;
                foreach (var client in pending.Clients) if (client.IsAlive) SendDetails(pending.Planet, client);
                detailRequests.Remove(pending.Planet.id);
            }
        }
        if (meshRepairPlanet == null || Time.realtimeSinceStartup < meshRepairAt) return;
        if (GameMain.localPlanet == meshRepairPlanet && meshRepairPlanet.factoryLoaded) RefreshMeshes(meshRepairPlanet);
        meshRepairPlanet = null;
    }

    public void Dispose()
    {
        HostPreparation.Clear();
        ModelLoader.Clear();
        ClearTerrainLoads();
        detailRequests.Clear();
        KnownFactoryPlanets.Clear();
        Loads.Clear();
        ReleasePackets();
        meshRepairPlanet = null;
        PendingFactories = null;
        PendingTerrainData = null;
        PendingBuildAssignments = null;
        pendingGenerations.Clear();
        bufferedFactories.Clear();
        PreservedDashboardData = null;
        GC.SuppressFinalize(this);
    }

    public static void UnloadAllFactories()
    {
        Multiplayer.Session.Planets.CancelTerrainLoads(GameMain.localStar);
        var gameData = GameMain.data;
        foreach (var planet in gameData.galaxy.stars.SelectMany(star => star.planets))
            if (Multiplayer.Session.Planets.Loads.Contains(planet.id)) Multiplayer.Session.Planets.CancelFactory(planet);
        Multiplayer.Session.Drones.ClearAllRemoteDrones();
        Multiplayer.Session.BuildDispatch.OnFactoriesUnloaded();
        Multiplayer.Session.Repairs?.AbortPlanet(null);
        using (Multiplayer.Session.Ships.PatchLockILS.On())
        {
            for (var i = gameData.factoryCount - 1; i >= 0; i--)
            {
                var planet = gameData.factories[i].planet;
                planet.factory.Free();
                planet.factory = null;
                gameData.galaxy.astrosFactory[planet.id] = null;  //Assigned by UpdateRuntimePose
            }
            gameData.factoryCount = 0;
            Multiplayer.Session.Combat.OnAstroFactoryUnload();
        }
        // Temporarily clear all CustomCharts on the unloaded factories to avoid errors, but preserve layout
        if (gameData.statistics?.charts != null)
        {
            try
            {
                using var ms = new System.IO.MemoryStream();
                using (var writer = new System.IO.BinaryWriter(ms))
                {
                    gameData.statistics.charts.Export(writer);
                }
                PreservedDashboardData = ms.ToArray();
            }
            catch (Exception e)
            {
                Log.Warn($"Failed to snapshot charts before factory unload: {e}");
            }
        }
        gameData.statistics?.charts?.Free();
        gameData.statistics?.charts?.Init(gameData);
    }
}
