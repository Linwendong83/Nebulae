#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe;

internal class DysonSailBatchProcessor : PacketProcessor<DysonSailBatchPacket>
{
    protected override void ProcessPacket(DysonSailBatchPacket packet, NebulaeConnection conn)
    {
        if (!packet.IsConsistent())
        {
            return;
        }
        var dysonSphere = GameMain.data.dysonSpheres[packet.StarIndex];
        if (dysonSphere == null)
        {
            return;
        }
        using (Multiplayer.Session.DysonSpheres.IncomingDysonSwarmPacket.On())
        {
            for (var i = 0; i < packet.OrbitId.Length; i++)
            {
                DysonSail ss = default;
                ss.px = packet.px[i];
                ss.py = packet.py[i];
                ss.pz = packet.pz[i];
                ss.vx = packet.vx[i];
                ss.vy = packet.vy[i];
                ss.vz = packet.vz[i];
                ss.gs = packet.gs[i];
                dysonSphere.swarm.AddSolarSail(ss, packet.OrbitId[i], packet.ExpiryTime[i]);
            }
        }
        if (IsHost)
        {
            Multiplayer.Session.DysonSpheres.SendPacketToDysonSphereExcept(packet, packet.StarIndex, conn);
        }
    }
}
