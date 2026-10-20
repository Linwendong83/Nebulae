using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics.ControlPanel;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Logistics.ControlPanel;

internal sealed class FacilityDirectoryRequestProcessor : PacketProcessor<FacilityDirectoryRequest>
{
    protected override void ProcessPacket(FacilityDirectoryRequest packet, NebulaeConnection conn)
    {
        if (IsHost && Players.Get(conn) != null) Multiplayer.Session.FacilityDirectory.Subscribe(conn, packet.Subscribe);
    }
}
internal sealed class FacilityDirectoryPacketProcessor : PacketProcessor<FacilityDirectoryPacket>
{
    protected override void ProcessPacket(FacilityDirectoryPacket packet, NebulaeConnection conn)
    {
        if (IsClient) Multiplayer.Session.FacilityDirectory.Receive(packet);
    }
}
internal sealed class FacilityRowsSubscriptionProcessor : PacketProcessor<FacilityRowsSubscription>
{
    protected override void ProcessPacket(FacilityRowsSubscription packet, NebulaeConnection conn)
    {
        if (!IsHost || Players.Get(conn) == null) return;
        var manager = Multiplayer.Session.FacilityDirectory;
        manager.SendRow = LCPObjectEntryRequestProcessor.SendEntry;
        manager.SetRows(conn, packet);
    }
}
