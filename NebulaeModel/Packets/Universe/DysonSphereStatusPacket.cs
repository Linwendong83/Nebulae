#region

using NebulaeModel.Networking;

#endregion

namespace NebulaeModel.Packets.Universe;

[NebulaeModel.Networking.PacketId(15870159248269640633UL)]
internal class DysonSphereStatusPacket
{
    public DysonSphereStatusPacket() { }

    public DysonSphereStatusPacket(DysonSphere dysonSphere)
    {
        StarIndex = dysonSphere.starData.index;
        EnergyReqCurrentTick = dysonSphere.energyReqCurrentTick;
        DebuffCoef = dysonSphere.energyDFHivesDebuffCoef;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }

    /// <summary>
    ///     Power demand aggregated across every planet of the star. Only the host simulates all of
    ///     them, so this is the one dyson-power number a peer cannot derive for itself.
    /// </summary>
    [NebulaeModel.Networking.WireOrder(1)]
    public long EnergyReqCurrentTick { get; set; }

    /// <summary>
    ///     Hive gamma debuff coefficient. It is a function of the host's gamma counts, which the
    ///     replica does not carry, so it travels as well; it only moves when a hive gains or loses a
    ///     gamma. Everything else about dyson energy (swarm, layers, gross radius) is derived from
    ///     the sphere structure the peer already replicates, and is computed locally.
    /// </summary>
    [NebulaeModel.Networking.WireOrder(2)]
    public double DebuffCoef { get; set; }
}
