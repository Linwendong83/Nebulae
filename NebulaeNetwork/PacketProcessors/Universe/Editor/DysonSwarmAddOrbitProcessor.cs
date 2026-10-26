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
        var spheres = GameMain.data?.dysonSpheres;
        if (spheres == null || packet.StarIndex < 0 || packet.StarIndex >= spheres.Length ||
            packet.OrbitId < 1 || packet.OrbitId > 20 || float.IsNaN(packet.Radius) ||
            float.IsInfinity(packet.Radius) || packet.Radius <= 0 || !ValidRotation(packet.Rotation)) return;
        var sphere = spheres[packet.StarIndex];
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

    internal static bool ValidRotation(Float4 value)
    {
        var length = (double)value.x * value.x + (double)value.y * value.y +
            (double)value.z * value.z + (double)value.w * value.w;
        return !double.IsNaN(length) && !double.IsInfinity(length) && length > .99 && length < 1.01;
    }
}
