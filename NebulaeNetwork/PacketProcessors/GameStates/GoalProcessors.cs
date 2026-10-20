using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameStates;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.GameStates;
internal class GoalSnapshotProcessor : PacketProcessor<GoalSnapshotPacket>
{
    protected override void ProcessPacket(GoalSnapshotPacket packet, NebulaeConnection conn)
    {
        if (IsClient && Multiplayer.Session.IsGameLoaded) Multiplayer.Session.Goals.Receive(packet);
    }
}
internal class GoalObservationProcessor : PacketProcessor<GoalObservationPacket>
{
    protected override void ProcessPacket(GoalObservationPacket packet, NebulaeConnection conn)
    {
        if (!IsHost) return;
        var player = Players.Get(conn);
        if (player != null) Multiplayer.Session.Goals.Observe(player.Id, packet);
    }
}
