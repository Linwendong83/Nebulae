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
        var spheres = GameMain.data?.dysonSpheres;
        if (spheres == null || packet.StarIndex < 0 || packet.StarIndex >= spheres.Length) return;
        if (IsHost)
        {
            var player = conn == null ? null : Players.Get(conn);
            if (!GameMain.data.gameDesc.isSandboxMode || player?.Data.LocalStarId != packet.StarIndex + 1) return;
        }
        var dysonSphere = spheres[packet.StarIndex];
        if (dysonSphere == null)
        {
            return;
        }
        // Validate the entire batch before mutating the swarm.
        for (var i = 0; i < packet.OrbitId.Length; i++)
        {
            if (!dysonSphere.swarm.OrbitExist(packet.OrbitId[i]) ||
                !Finite(packet.px[i]) || !Finite(packet.py[i]) || !Finite(packet.pz[i]) ||
                !Finite(packet.vx[i]) || !Finite(packet.vy[i]) || !Finite(packet.vz[i]) ||
                !Finite(packet.gs[i]) || packet.gs[i] <= 0) return;
        }
        if (IsHost)
        {
            var expiry = GameMain.gameTick + (long)(GameMain.history.solarSailLife * 60f + .1f);
            for (var i = 0; i < packet.ExpiryTime.Length; i++) packet.ExpiryTime[i] = expiry;
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

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
