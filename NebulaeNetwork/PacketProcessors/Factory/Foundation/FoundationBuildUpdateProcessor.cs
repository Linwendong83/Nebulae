#region

using System;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Foundation;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Foundation;
internal class FoundationBuildUpdateProcessor : PacketProcessor<FoundationBuildUpdatePacket>
{
    private Vector3[] reformPoints = new Vector3[400];

    protected override void ProcessPacket(FoundationBuildUpdatePacket packet, NebulaeConnection conn)
    {
        if (!ValidBrush(packet)) return;

        var planet = GameMain.galaxy.PlanetById(packet.PlanetId);
        var factory = planet?.factory;
        if (factory == null) return;
        ushort author = 0;
        if (IsHost)
        {
            var player = Players.Get(conn, EConnectionStatus.Connected);
            if (player == null || player.Data.LocalPlanetId != packet.PlanetId) return;
            if (packet.PitRuinId > 0 && (!factory.GetRemovableDFGBaseRuinIds().Contains(packet.PitRuinId) ||
                packet.PitRuinGeneration <= 0 || Multiplayer.Session.AuthorityRuntime.RuinHost.Generation(packet.PlanetId, packet.PitRuinId) != packet.PitRuinGeneration)) return;
            author = player.Id;
            if (!Multiplayer.Session.Vegetation.AcceptCommand(author, packet.CommandSequence)) return;
            var collection = Multiplayer.Session.Vegetation.GetRemote(author);
            if (collection == null) return;
            var canonicalBefore = VegetableCollectionState.Capture(collection);
            packet.CollectionBefore = canonicalBefore;
        }
        else Multiplayer.Session.Vegetation.BeginReplay(packet.CollectionBefore);

        var previousAuthor = Multiplayer.Session.Factories.PacketAuthor;
        var previousPlanet = Multiplayer.Session.Factories.TargetPlanet;
        var previousSpecify = GameMain.gpuiManager.specifyPlanet;
        try
        {
            Multiplayer.Session.Factories.PacketAuthor = author;
            GameMain.gpuiManager.specifyPlanet = planet;
            // Circle buffers have 400 entries in vanilla even though the search size is 40.
            // Recompute into a full scratch buffer rather than treating it as a 40x40 brush.
            if (packet.IsCircle) packet.ReformIndices = new int[packet.ReformSize * packet.ReformSize];
            // Increase reformPoints for mods that increase brush size over 10
            if (Math.Max(packet.ReformSize * packet.ReformSize, packet.CirclePointCount) > reformPoints.Length)
            {
                reformPoints = new Vector3[Math.Max(packet.ReformSize * packet.ReformSize, packet.CirclePointCount)];
            }
            Array.Clear(reformPoints, 0, reformPoints.Length);

            //Check if some mandatory variables are missing
            if (factory.platformSystem.reformData == null)
            {
                factory.platformSystem.InitReformData();
            }
            foreach (var index in packet.ReformIndices)
                if (index < -1 || index >= factory.platformSystem.reformData.Length) return;

            Multiplayer.Session.Factories.TargetPlanet = packet.PlanetId;
            Multiplayer.Session.Factories.AddPlanetTimer(packet.PlanetId);

            //Perform terrain operation
            var center = packet.ExtraCenter.ToVector3();
            var area = packet.CirclePointCount;
            var costSandCount = 0;
            var getSandCount = 0;
            if (!packet.IsCircle) //Normal reform
            {
                var reformPointsCount = factory.planet.aux.ReformSnap(packet.GroundTestPos.ToVector3(), packet.ReformMode,
                    packet.ReformSize,
                    packet.ReformType, packet.ReformColor, reformPoints, packet.ReformIndices, factory.platformSystem,
                    out var reformCenterPoint);
                if (packet.IsRestore)
                    factory.ComputeRestoreTerrainReform(reformPoints, packet.ReformIndices,
                        packet.ReformSize * packet.ReformSize, reformCenterPoint, packet.Radius,
                        reformPointsCount, ref costSandCount, ref getSandCount);
                else
                    factory.ComputeFlattenTerrainReform(reformPoints, reformCenterPoint, packet.Radius,
                        reformPointsCount, ref costSandCount, ref getSandCount);
                center = reformCenterPoint;
                area = packet.ReformSize * packet.ReformSize;
            }
            else //Remove pit
            {
                area = planet.aux.ReformSnapCircle(packet.ReformSize, packet.ReformType, packet.ReformColor,
                    center, packet.Radius * packet.Radius, reformPoints, packet.ReformIndices, null, factory.platformSystem);
                packet.CirclePointCount = area;
                factory.ComputeFlattenTerrainReform(reformPoints, center, packet.Radius, area,
                    ref costSandCount, ref getSandCount, 3f, 1f);
            }
            using (Multiplayer.Session.Factories.IsIncomingRequest.On())
            {
                if (packet.PitRuinId > 0 && (IsHost ||
                    Multiplayer.Session.AuthorityRuntime.WorldReplica.TryFindKey(NebulaeModel.Authority.PoolKind.Ruin, packet.PlanetId, packet.PitRuinId, out var pitKey) &&
                    (packet.PitRuinGeneration == 0 || pitKey.Generation == packet.PitRuinGeneration)))
                    factory.enemySystem.RemoveBasePit(packet.PitRuinId);
                if (packet.IsRestore)
                    factory.RestoreTerrainReform(center, packet.Radius, packet.ReformSize, packet.VeinBuried,
                        packet.Fade0);
                else
                    factory.FlattenTerrainReform(center, packet.Radius, packet.ReformSize, packet.VeinBuried,
                        packet.Fade0);
            }
            if (!packet.IsRestore)
            {
                var platformSystem = factory.platformSystem;
                for (var i = 0; i < area; i++)
                {
                    var index = packet.ReformIndices[i];
                    if (index < 0)
                    {
                        continue;
                    }
                    var type = platformSystem.GetReformType(index);
                    var color = platformSystem.GetReformColor(index);
                    if (type != packet.ReformType || color != packet.ReformColor)
                    {
                        factory.platformSystem.SetReformType(index, packet.ReformType);
                        factory.platformSystem.SetReformColor(index, packet.ReformColor);
                    }
                }
            }
        }
        finally
        {
            Multiplayer.Session.Factories.PacketAuthor = previousAuthor;
            Multiplayer.Session.Factories.TargetPlanet = previousPlanet;
            GameMain.gpuiManager.specifyPlanet = previousSpecify;
            if (!IsHost) Multiplayer.Session.Vegetation.EndReplay();
        }

        if (IsHost)
        {
            Multiplayer.Session.Server.SendPacketToStarExclude(packet, planet.star.id, conn);
            Multiplayer.Session.Vegetation.CaptureRemote(author);
        }
    }

    internal static bool ValidBrush(FoundationBuildUpdatePacket packet) => packet != null &&
        !(packet.ReformSize <= 0 || packet.ReformSize > 64 || packet.ReformIndices == null ||
          packet.CollectionBefore == null || packet.CollectionBefore.Length > VegetableCollectionState.MaxBytes ||
          packet.ReformIndices.Length > 4096 || packet.IsRestore && packet.IsCircle ||
          float.IsNaN(packet.Radius) || float.IsInfinity(packet.Radius) || packet.Radius <= 0 || packet.Radius > 256 ||
          float.IsNaN(packet.Fade0) || float.IsInfinity(packet.Fade0) || packet.Fade0 < 0 || packet.Fade0 > 64 ||
          packet.PitRuinId < 0 || packet.PitRuinGeneration < 0 || !packet.IsCircle && (packet.PitRuinId != 0 || packet.PitRuinGeneration != 0) ||
          packet.ReformType < 0 || packet.ReformType > 7 || packet.ReformColor < 0 || packet.ReformColor > 31 ||
          !packet.IsCircle && packet.ReformIndices.Length < packet.ReformSize * packet.ReformSize ||
          !Finite(packet.GroundTestPos) || !Finite(packet.ExtraCenter) ||
          packet.IsCircle && (packet.CirclePointCount < 0 || packet.CirclePointCount > packet.ReformIndices.Length));

    private static bool Finite(Float3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
}
