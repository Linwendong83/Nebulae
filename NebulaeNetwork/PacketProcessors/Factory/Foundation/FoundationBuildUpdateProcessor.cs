#region

using System;
using System.Linq;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Foundation;
using NebulaeModel.Packets.Players;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Foundation;
internal class FoundationBuildUpdateProcessor : PacketProcessor<FoundationBuildUpdatePacket>
{
    private Vector3[] reformPoints = new Vector3[400];

    protected override void ProcessPacket(FoundationBuildUpdatePacket packet, NebulaeConnection conn)
    {
        if (packet.ReformSize <= 0 || packet.ReformSize > 64 || packet.ReformIndices == null ||
            packet.CollectionBefore == null || packet.CollectionBefore.Length > VegetableCollectionState.MaxBytes ||
            packet.ReformIndices.Length > 4096 || packet.IsRestore && packet.IsCircle ||
            !packet.IsCircle && packet.ReformIndices.Length < packet.ReformSize * packet.ReformSize ||
            packet.IsCircle && (packet.CirclePointCount < 0 || packet.CirclePointCount > packet.ReformIndices.Length))
            return;

        var planet = GameMain.galaxy.PlanetById(packet.PlanetId);
        var factory = planet?.factory;
        if (factory == null) return;
        ushort author = 0;
        if (IsHost)
        {
            var player = Players.Get(conn, EConnectionStatus.Connected);
            if (player == null || player.Data.LocalStarId != planet.star.id) return;
            author = player.Id;
            var collection = Multiplayer.Session.Vegetation.GetRemote(author);
            if (collection == null) return;
            var canonicalBefore = VegetableCollectionState.Capture(collection);
            if (!packet.CollectionBefore.SequenceEqual(canonicalBefore))
            {
                conn.SendPacket(new VegetableCollectionSnapshotPacket(canonicalBefore, true));
                Multiplayer.Session.Server.Disconnect(conn, DisconnectionReason.InvalidData,
                    "Vegetation collection is out of sync. Reconnect to reload the planet.");
                return;
            }
            packet.CollectionBefore = canonicalBefore;
        }
        else Multiplayer.Session.Vegetation.BeginReplay(packet.CollectionBefore);

        try
        {
            Multiplayer.Session.Factories.PacketAuthor = author;
            // Increase reformPoints for mods that increase brush size over 10
            if (packet.ReformSize * packet.ReformSize > reformPoints.Length)
            {
                reformPoints = new Vector3[packet.ReformSize * packet.ReformSize];
            }
            Array.Clear(reformPoints, 0, reformPoints.Length);

            //Check if some mandatory variables are missing
            if (factory.platformSystem.reformData == null)
            {
                factory.platformSystem.InitReformData();
            }

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
                factory.ComputeFlattenTerrainReform(reformPoints, center, packet.Radius, packet.CirclePointCount,
                    ref costSandCount, ref getSandCount, 3f, 1f);
            }
            using (Multiplayer.Session.Factories.IsIncomingRequest.On())
            {
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
            Multiplayer.Session.Factories.PacketAuthor = SessionSentinels.AUTHOR_NONE;
            Multiplayer.Session.Factories.TargetPlanet = SessionSentinels.PLANET_NONE;
            if (!IsHost) Multiplayer.Session.Vegetation.EndReplay();
        }

        if (IsHost)
        {
            Multiplayer.Session.Server.SendPacketToStarExclude(packet, planet.star.id, conn);
            var authoritative = Multiplayer.Session.Vegetation.CaptureRemote(author);
            conn.SendPacket(new VegetableCollectionSnapshotPacket(authoritative, true));
        }
    }
}
