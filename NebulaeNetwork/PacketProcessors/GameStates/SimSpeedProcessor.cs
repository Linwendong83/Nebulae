#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameStates;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.GameStates;

internal class SimSpeedProcessor : PacketProcessor<SimSpeedPacket>
{
    protected override void ProcessPacket(SimSpeedPacket packet, NebulaeConnection conn)
    {
        if (!IsHost)
        {
            return;
        }

        // The host owns the world speed. The requested value only has to stay inside the vanilla
        // dial range: the host's fixUPS is untouched, and the clients track the host's logic rate
        // through the speed-compensated UPS sync at any speed.
        var upsTool = UIRoot.instance?.uiGame?.upsTool;
        if (upsTool == null)
        {
            return;
        }
        var speed = Math.Min(Math.Max(packet.Speed, 0.1f), 8f);
        // Set both the tool's target and the applied value: the vanilla tool lerps the applied
        // value toward the target every frame while it is open, and the two must not fight.
        upsTool.targetSimSpeed = speed;
        FPSController.SimulateSpeed = speed;
    }
}
