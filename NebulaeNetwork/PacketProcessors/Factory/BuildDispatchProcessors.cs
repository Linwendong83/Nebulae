using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Factory;
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
        if (!IsHost) return;
        var player = Players.Get(conn);
        if (player != null && player.Data.LocalPlanetId == packet.PlanetId)
            Multiplayer.Session.BuildDispatch.HandleReadyNotice(player.Id, packet);
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
