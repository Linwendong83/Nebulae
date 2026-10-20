#region

using System;
using System.Collections.Generic;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Foundation;
using NebulaeWorld;
using UnityEngine;
#pragma warning disable IDE0007 // Use implicit type

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Foundation;
internal class FoundationBlueprintPasteProcessor : PacketProcessor<FoundationBlueprintPastePacket>
{
    protected override void ProcessPacket(FoundationBlueprintPastePacket packet, NebulaeConnection conn)
    {
        var planet = GameMain.galaxy.PlanetById(packet.PlanetId);
        var factory = planet?.factory;
        if (factory == null || planet.data?.heightData == null) return;
        if (packet.LevelChangesKeys == null || packet.LevelChangesValues == null ||
            packet.LevelChangesKeys.Length != packet.LevelChangesValues.Length ||
            packet.LevelChangesKeys.Length > planet.data.heightData.Length ||
            packet.ReformData == null || packet.ReformGridIds == null ||
            packet.ReformData.Length > 4 * 1024 * 1024 || packet.ReformGridIds.Length > 1000000)
            return;
        var levelChanges = new Dictionary<int, int>();
        for (var i = 0; i < packet.LevelChangesKeys.Length; i++)
        {
            var index = packet.LevelChangesKeys[i];
            if (index < 0 || index >= planet.data.heightData.Length || levelChanges.ContainsKey(index)) return;
            levelChanges.Add(index, packet.LevelChangesValues[i]);
        }
        VegetableCollection collector = null;
        ushort author = 0;
        if (IsHost)
        {
            var player = Players.Get(conn, EConnectionStatus.Connected);
            if (player == null || player.Data.LocalPlanetId != packet.PlanetId) return;
            author = player.Id;
            collector = Multiplayer.Session.Vegetation.GetRemote(author);
            if (collector == null) return;
        }

        var specifyPlanet = GameMain.gpuiManager.specifyPlanet;
        try
        {
            using (Multiplayer.Session.Planets.IsIncomingRequest.On())
            {
                Multiplayer.Session.Factories.TargetPlanet = packet.PlanetId;
                Multiplayer.Session.Factories.AddPlanetTimer(packet.PlanetId);
                GameMain.gpuiManager.specifyPlanet = planet;

                // Split BuildTool_BlueprintPaste.DetermineReforms into following functions
                factory.platformSystem.EnsureReformData();
                if (packet.ReformData.Length > 0)
                    SetReformByData(factory, packet.ReformData);
                else if (packet.ReformGridIds.Length > 0)
                    SetReformByGridIds(factory, packet.ReformGridIds, packet.ReformType, packet.ReformColor);
                AlterHeightMap(planet, levelChanges);
                RemoveVeges(factory, levelChanges, collector);
                UpdateGeothermalStrength(factory);
                AlterVeinModels(factory);
            }
        }
        finally
        {
            GameMain.gpuiManager.specifyPlanet = specifyPlanet;
            Multiplayer.Session.Factories.TargetPlanet = SessionSentinels.PLANET_NONE;
        }
        if (IsHost)
        {
            Multiplayer.Session.Server.SendPacketToStarExclude(packet, planet.star.id, conn);
            Multiplayer.Session.Vegetation.CaptureRemote(author);
        }
    }

    static void SetReformByData(PlanetFactory factory, byte[] reformData)
    {
        // EBlueprintPasteResult.HasReform in DetermineReforms()
        var length = Math.Min(reformData.Length, factory.platformSystem.reformData.Length);
        Array.Copy(reformData, factory.platformSystem.reformData, length);
    }

    static void SetReformByGridIds(PlanetFactory factory, int[] reformGridIds, int brushType, int brushColor)
    {
        // EBlueprintPasteResult.BuildingNeedReform in DetermineReforms()
        var platformSystem = factory.platformSystem;
        foreach (int gridId in reformGridIds)
        {
            int reformIndex = platformSystem.GetReformIndex(gridId >> 16, gridId & 65535);
            if (reformIndex >= 0)
            {
                platformSystem.SetReformType(reformIndex, brushType);
                platformSystem.SetReformColor(reformIndex, brushColor);
            }
        }
    }

    static void AlterHeightMap(PlanetData planet, Dictionary<int, int> heightLevelChanges)
    {
        PlanetRawData planetRawData = planet.data;
        bool isPlanetLevelized = planet.levelized;

        // Calculate the maximum height threshold for terrain modifications
        int heightThreshold = (int)(planet.realRadius * 100f + 20f) - 60;

        ushort[] heightMap = planetRawData.heightData;

        foreach (KeyValuePair<int, int> heightChange in heightLevelChanges)
        {
            if (heightChange.Value > 0)
            {
                if (isPlanetLevelized)
                {
                    if (heightMap[heightChange.Key] >= heightThreshold)
                    {
                        if (planetRawData.GetModLevel(heightChange.Key) < 3)
                        {
                            planetRawData.SetModPlane(heightChange.Key, 0);
                        }
                        planet.AddHeightMapModLevel(heightChange.Key, heightChange.Value);
                    }
                }
                else
                {
                    planet.AddHeightMapModLevel(heightChange.Key, heightChange.Value);
                }
            }
        }

        bool meshesUpdated = planet.UpdateDirtyMeshes();
        if (GameMain.isRunning && meshesUpdated && planet == GameMain.localPlanet)
        {
            planet.factory?.RenderLocalPlanetHeightmap();
        }
    }

    static void RemoveVeges(PlanetFactory planetFactory, Dictionary<int, int> heightLevelChanges,
        VegetableCollection collector)
    {
        PlanetRawData planetRawData = planetFactory.planet.data;
        VegeData[] vegePool = planetFactory.vegePool;
        int vegeCursor = planetFactory.vegeCursor;

        for (int vegetationIndex = 1; vegetationIndex < vegeCursor; vegetationIndex++)
        {
            if (vegePool[vegetationIndex].id != 0)
            {
                int terrainIndex = planetRawData.QueryIndex(vegePool[vegetationIndex].pos);
                if (heightLevelChanges.TryGetValue(terrainIndex, out int modificationLevel) && modificationLevel >= 3)
                {
                    collector?.AddVegeToPlayer(vegePool[vegetationIndex].protoId, 1);
                    planetFactory.RemoveVegeWithComponents(vegetationIndex);
                }
            }
        }
    }

    static void UpdateGeothermalStrength(PlanetFactory factory)
    {
        if (factory.planet.waterItemId != -1) return;

        PowerSystem powerSystem = factory.powerSystem;
        PowerGeneratorComponent[] generatorPool = powerSystem.genPool;
        int generatorCount = powerSystem.genCursor;
        EntityData[] entityPool = factory.entityPool;

        for (int generatorIndex = 1; generatorIndex < generatorCount; generatorIndex++)
        {
            ref PowerGeneratorComponent generator = ref generatorPool[generatorIndex];
            if (generator.id == generatorIndex && generator.geothermal && generator.entityId > 0)
            {
                generator.gthStrength = powerSystem.CalculateGeothermalStrength(
                    entityPool[generator.entityId].pos,
                    entityPool[generator.entityId].rot,
                    generator.baseRuinId
                );
            }
        }
    }

    static void AlterVeinModels(PlanetFactory factory)
    {
        PlanetData planet = factory.planet;
        PlanetRawData data = planet.data;

        VeinData[] veinPool = factory.veinPool;
        int veinCursor = factory.veinCursor;
        float veinSurfaceThreshold = planet.realRadius - 50f + 5f;
        PlanetPhysics physics = planet.physics;
        for (int veinId = 1; veinId < veinCursor; veinId++)
        {
            ref VeinData veinPtr = ref veinPool[veinId];
            if (veinPtr.id == veinId)
            {
                Vector3 pos = veinPtr.pos;
                float magnitude = pos.magnitude;
                if (magnitude >= veinSurfaceThreshold)
                {
                    float veinHeight = data.QueryModifiedHeight(pos) - 0.13f;
                    int colliderId = veinPtr.colliderId;
                    Vector3 vector = physics.GetColliderData(colliderId).pos.normalized * (veinHeight + 0.4f);
                    int chunkId = colliderId >> 20;
                    colliderId &= 1048575;
                    physics.colChunks[chunkId].colliderPool[colliderId].pos = vector;
                    Vector3 vectorNormal = pos / magnitude;
                    veinPtr.pos = vectorNormal * veinHeight;
                    physics.SetPlanetPhysicsColliderDirty();
                    if (Mathf.Abs(magnitude - veinSurfaceThreshold) > 0.1f)
                    {
                        Quaternion quaternion = Maths.SphericalRotation(pos, UnityEngine.Random.value * 360f);
                        GameMain.gpuiManager.AlterModel(veinPtr.modelIndex, veinPtr.modelId, veinId, pos, quaternion, false);
                    }
                    else
                    {
                        GameMain.gpuiManager.AlterModel(veinPtr.modelIndex, veinPtr.modelId, veinId, pos, false);
                    }
                    VeinProto veinProto = LDB.veins.Select((int)veinPool[veinId].type);
                    if (veinProto != null)
                    {
                        if (veinPool[veinId].minerId0 > 0)
                        {
                            GameMain.gpuiManager.AlterModel(veinProto.MinerBaseModelIndex, veinPtr.minerBaseModelId, veinPtr.minerId0, vectorNormal * (veinHeight + 0.1f), false);
                            GameMain.gpuiManager.AlterModel(veinProto.MinerCircleModelIndex, veinPtr.minerCircleModelId0, veinPtr.minerId0, vectorNormal * (veinHeight + 0.4f), false);
                        }
                        if (veinPool[veinId].minerId1 > 0)
                        {
                            GameMain.gpuiManager.AlterModel(veinProto.MinerCircleModelIndex, veinPtr.minerCircleModelId1, veinPtr.minerId1, vectorNormal * (veinHeight + 0.6f), false);
                        }
                        if (veinPool[veinId].minerId2 > 0)
                        {
                            GameMain.gpuiManager.AlterModel(veinProto.MinerCircleModelIndex, veinPtr.minerCircleModelId2, veinPtr.minerId2, vectorNormal * (veinHeight + 0.8f), false);
                        }
                        if (veinPool[veinId].minerId3 > 0)
                        {
                            GameMain.gpuiManager.AlterModel(veinProto.MinerCircleModelIndex, veinPtr.minerCircleModelId3, veinPtr.minerId3, vectorNormal * (veinHeight + 1f), false);
                        }
                    }
                }
            }
        }
    }


}
