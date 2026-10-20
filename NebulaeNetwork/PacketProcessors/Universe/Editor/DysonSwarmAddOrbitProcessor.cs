#region

using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeWorld;
using NebulaeWorld.Universe;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe.Editor;
internal class DysonSwarmAddOrbitProcessor : PacketProcessor<DysonSwarmAddOrbitPacket>
{
    protected override void ProcessPacket(DysonSwarmAddOrbitPacket packet, NebulaeConnection conn)
    {
        var sphere = GameMain.data.dysonSpheres[packet.StarIndex];
        if (sphere == null)
        {
            return;
        }
        using (Multiplayer.Session.DysonSpheres.IncomingDysonSwarmPacket.On())
        {
            if (packet.OrbitId != DysonSphereManager.QueryOrbitId(sphere.swarm))
            {
                Multiplayer.Session.DysonSpheres.HandleDesync(packet.StarIndex, conn);
                return;
            }
            sphere.swarm.NewOrbit(packet.Radius, packet.Rotation.ToQuaternion());
        }
        if (IsHost)
        {
            Multiplayer.Session.DysonSpheres.SendPacketToDysonSphereExcept(packet, packet.StarIndex, conn);
        }
    }
}
