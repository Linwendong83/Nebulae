using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Factory;
internal sealed class ConstructionLaunchOfferProcessor : PacketProcessor<ConstructionLaunchOfferPacket>
{
    protected override void ProcessPacket(ConstructionLaunchOfferPacket packet, NebulaeConnection conn)
    {
        if (!IsHost) return;
        var player = Players.Get(conn);
        if (player?.Data.LocalPlanetId == packet.PlanetId) Multiplayer.Session.BuildDispatch.ReceiveOffer(player.Id, packet);
    }
}
internal sealed class ConstructionLaunchGrantProcessor : PacketProcessor<ConstructionLaunchGrantPacket>
{
    protected override void ProcessPacket(ConstructionLaunchGrantPacket packet, NebulaeConnection conn)
    {
        if (!IsHost) Multiplayer.Session.BuildDispatch.ReceiveGrant(packet);
    }
}
internal sealed class BaseDroneStateProcessor : PacketProcessor<BaseDroneStatePacket>
{
    protected override void ProcessPacket(BaseDroneStatePacket packet, NebulaeConnection conn)
    {
        if (!IsHost) Multiplayer.Session.BuildDispatch.ReceiveBaseDrones(packet);
    }
}
internal sealed class DroneFlightSnapshotRequestProcessor : PacketProcessor<DroneFlightSnapshotRequestPacket>
{
    protected override void ProcessPacket(DroneFlightSnapshotRequestPacket packet, NebulaeConnection conn)
    {
        if (!IsHost) return;
        var player = Players.Get(conn);
        var planet = GameMain.galaxy?.PlanetById(packet.PlanetId);
        if (planet?.factory == null || player?.Data.LocalStarId != planet.star.id) return;
        Multiplayer.Session.BuildDispatch.SendBaseDroneSnapshot(planet.factory, conn);
        Multiplayer.Session.Repairs.SendSnapshot(planet.factory, conn);
    }
}
internal sealed class ConstructionStorageProcessor : PacketProcessor<ConstructionStoragePacket>
{
    protected override void ProcessPacket(ConstructionStoragePacket packet, NebulaeConnection conn)
    {
        if (!IsHost) Multiplayer.Session.BuildDispatch.ReceiveConstructionStorage(packet);
    }
}
internal sealed class PrebuildInstanceProcessor : PacketProcessor<PrebuildInstancePacket>
{
    protected override void ProcessPacket(PrebuildInstancePacket packet, NebulaeConnection conn)
    {
        if (!IsHost) Multiplayer.Session.BuildDispatch.Instances.Apply(packet);
    }
}

internal sealed class BuildTargetAssignmentProcessor : PacketProcessor<BuildTargetAssignmentPacket>
{
    protected override void ProcessPacket(BuildTargetAssignmentPacket packet, NebulaeConnection conn)
    {
        if (IsHost) return;
        Multiplayer.Session.BuildDispatch.ReceiveAssignment(packet);
    }
}
internal sealed class BuildTargetAssignmentReplyProcessor : PacketProcessor<BuildTargetAssignmentReplyPacket>
{
    protected override void ProcessPacket(BuildTargetAssignmentReplyPacket packet, NebulaeConnection conn)
    {
        if (!IsHost) return;
        var player = Players.Get(conn);
        if (player != null) Multiplayer.Session.BuildDispatch.HandleReply(player.Id, packet);
    }
}
internal sealed class BuildTargetReadyProcessor : PacketProcessor<BuildTargetReadyPacket>
{
    protected override void ProcessPacket(BuildTargetReadyPacket packet, NebulaeConnection conn)
    {
        // Superseded by a native next-flight offer. Readiness alone never owns a site.
    }
}
internal sealed class BuildTargetBaseReleaseAckProcessor : PacketProcessor<BuildTargetBaseReleaseAckPacket>
{
    protected override void ProcessPacket(BuildTargetBaseReleaseAckPacket packet, NebulaeConnection conn)
    {
        if (!IsHost) return;
        var player = Players.Get(conn);
        if (player != null) Multiplayer.Session.BuildDispatch.HandleBaseReleaseAck(player.Id, packet);
    }
}
internal sealed class BuildDroneLaunchProcessor : PacketProcessor<BuildDroneLaunchPacket>
{
    protected override void ProcessPacket(BuildDroneLaunchPacket packet, NebulaeConnection conn)
    {
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        if (factory == null) return;
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player == null || !Multiplayer.Session.BuildDispatch.ValidateAndRecordMechaLaunch(packet, player.Id))
                return;
            Multiplayer.Session.Server.SendPacketToStar(packet, factory.planet.star.id);
        }
        else if (!Multiplayer.Session.BuildDispatch.ReceiveValidatedLaunch(packet)) return;

        if (packet.PlayerId == Multiplayer.Session.LocalPlayer.Id ||
            !Multiplayer.Session.BuildDispatch.TryRenderRemoteLaunch(packet)) return;
        Multiplayer.Session.Drones.EjectMechaDroneFromOtherPlayer(packet.PlayerId, packet.PlanetId,
            packet.TargetObjectId, packet.Next1ObjectId, packet.Next2ObjectId, packet.Next3ObjectId, packet.DronePriority);
    }
}
