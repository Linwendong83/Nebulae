#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe;
internal class DysonSphereStatusProcessor : PacketProcessor<DysonSphereStatusPacket>
{
    protected override void ProcessPacket(DysonSphereStatusPacket packet, NebulaeConnection conn)
    {
        var spheres = GameMain.data?.dysonSpheres;
        if (IsHost || spheres == null || packet.StarIndex < 0 || packet.StarIndex >= spheres.Length) return;
        var dysonSphere = spheres[packet.StarIndex];
        if (IsHost || dysonSphere == null)
        {
            return;
        }
        // Launch replay is presentation and can lag the host. Never derive power from that replica.
        packet.Apply(dysonSphere);
    }
}
