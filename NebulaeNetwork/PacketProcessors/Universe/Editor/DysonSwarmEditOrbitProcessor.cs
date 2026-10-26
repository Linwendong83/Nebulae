#region

using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe.Editor;
internal class DysonSwarmEditOrbitProcessor : PacketProcessor<DysonSwarmEditOrbitPacket>
{
    protected override void ProcessPacket(DysonSwarmEditOrbitPacket packet, NebulaeConnection conn)
    {
        var spheres = GameMain.data?.dysonSpheres;
        if (spheres == null || packet.StarIndex < 0 || packet.StarIndex >= spheres.Length ||
            packet.OrbitId < 1 || packet.OrbitId > 30 || float.IsNaN(packet.Radius) || float.IsInfinity(packet.Radius)) return;
        if (packet.Radius >= 0 && !DysonSwarmAddOrbitProcessor.ValidRotation(packet.Rotation)) return;
        if (packet.Radius < 0 && (!Finite(packet.Color.x) || !Finite(packet.Color.y) || !Finite(packet.Color.z) || !Finite(packet.Color.w))) return;
        var sphere = spheres[packet.StarIndex];
        if (sphere == null)
        {
            return;
        }
        using (Multiplayer.Session.DysonSpheres.IncomingDysonSwarmPacket.On())
        {
            if (!sphere.swarm.OrbitExist(packet.OrbitId))
            {
                Multiplayer.Session.DysonSpheres.HandleDesync(packet.StarIndex, conn);
                return;
            }
            if (packet.Radius >= 0)
            {
                sphere.swarm.EditOrbit(packet.OrbitId, packet.Radius, packet.Rotation.ToQuaternion());
            }
            else
            {
                sphere.swarm.SetOrbitColor(packet.OrbitId,
                    new Vector4(packet.Color.x, packet.Color.y, packet.Color.z, packet.Color.w));
            }
        }
        if (IsHost)
        {
            Multiplayer.Session.DysonSpheres.SendPacketToDysonSphereExcept(packet, packet.StarIndex, conn);
        }
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
