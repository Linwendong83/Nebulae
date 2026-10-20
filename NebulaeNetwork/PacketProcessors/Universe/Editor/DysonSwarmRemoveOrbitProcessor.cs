#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeWorld;
using NebulaeWorld.Universe;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe.Editor;
internal class DysonSwarmRemoveOrbitProcessor : PacketProcessor<DysonSwarmRemoveOrbitPacket>
{
    protected override void ProcessPacket(DysonSwarmRemoveOrbitPacket packet, NebulaeConnection conn)
    {
        var sphere = GameMain.data.dysonSpheres[packet.StarIndex];
        if (sphere == null)
        {
            return;
        }
        using (Multiplayer.Session.DysonSpheres.IncomingDysonSwarmPacket.On())
        {
            switch (packet.Event)
            {
                case SwarmRemoveOrbitEvent.Remove:
                    sphere.swarm.RemoveOrbit(packet.OrbitId);
                    break;
                case SwarmRemoveOrbitEvent.Enable:
                case SwarmRemoveOrbitEvent.Disable:
                    sphere.swarm.SetOrbitEnable(packet.OrbitId, packet.Event == SwarmRemoveOrbitEvent.Enable);
                    break;
                case SwarmRemoveOrbitEvent.RemoveSails:
                    sphere.swarm.RemoveSailsByOrbit(packet.OrbitId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(packet), "Unknown SwarmRemoveOrbitEvent type: " + packet.Event);
            }
            DysonSphereManager.ClearSelection(packet.StarIndex);
        }
        if (IsHost)
        {
            Multiplayer.Session.DysonSpheres.SendPacketToDysonSphereExcept(packet, packet.StarIndex, conn);
        }
    }
}
