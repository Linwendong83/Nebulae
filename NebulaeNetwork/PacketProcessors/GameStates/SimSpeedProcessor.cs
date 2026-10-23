#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameStates;
using NebulaeWorld;
using NebulaeWorld.GameStates;

#endregion

namespace NebulaeNetwork.PacketProcessors.GameStates;

internal class SimSpeedProcessor : PacketProcessor<SimSpeedPacket>
{
    protected override void ProcessPacket(SimSpeedPacket packet, NebulaeConnection conn)
    {
        if (float.IsNaN(packet.Speed) || float.IsInfinity(packet.Speed)) return;
        var speed = Math.Min(Math.Max(packet.Speed, 0.1f), 8f);
        GameStatesManager.ApplyWorldSimSpeed(speed);
        if (IsHost) Multiplayer.Session.Server?.SendPacket(new SimSpeedPacket(speed));
    }
}
