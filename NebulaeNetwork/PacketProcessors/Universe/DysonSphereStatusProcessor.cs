#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe;
internal class DysonSphereStatusProcessor : PacketProcessor<DysonSphereStatusPacket>
{
    protected override void ProcessPacket(DysonSphereStatusPacket packet, NebulaeConnection conn)
    {
        var dysonSphere = GameMain.data.dysonSpheres[packet.StarIndex];
        if (IsHost || dysonSphere == null)
        {
            return;
        }
        // Ownership split: the demand aggregated across every planet, and the hive gamma debuff,
        // are the only two facts this peer cannot compute. Everything else about the sphere's energy
        // is derived from the structure it replicates and is recomputed locally in
        // DysonSphere.BeforeGameTick, so applying it here would overwrite fresher local work.
        dysonSphere.energyReqCurrentTick = packet.EnergyReqCurrentTick;
        Multiplayer.Session.DysonSpheres.SetDebuffCoef(packet.StarIndex, packet.DebuffCoef);
    }
}
