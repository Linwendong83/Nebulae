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
        EnergyGenCurrentTick = dysonSphere.energyGenCurrentTick;
        EnergyGenOriginalCurrentTick = dysonSphere.energyGenOriginalCurrentTick;
        SwarmEnergyGenCurrentTick = dysonSphere.swarm.energyGenCurrentTick;
        GrossRadius = dysonSphere.grossRadius;
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
    ///     gamma. Generation totals below are authoritative too: launch replay can lag the host.
    /// </summary>
    [NebulaeModel.Networking.WireOrder(2)]
    public double DebuffCoef { get; set; }
    [WireOrder(3)]
    public long EnergyGenCurrentTick { get; set; }
    [WireOrder(4)]
    public long EnergyGenOriginalCurrentTick { get; set; }
    [WireOrder(5)]
    public long SwarmEnergyGenCurrentTick { get; set; }
    [WireOrder(6)]
    public float GrossRadius { get; set; }

    public bool SameValues(DysonSphereStatusPacket other) => EnergyReqCurrentTick == other.EnergyReqCurrentTick &&
        DebuffCoef == other.DebuffCoef && EnergyGenCurrentTick == other.EnergyGenCurrentTick &&
        EnergyGenOriginalCurrentTick == other.EnergyGenOriginalCurrentTick &&
        SwarmEnergyGenCurrentTick == other.SwarmEnergyGenCurrentTick && GrossRadius == other.GrossRadius;

    public void Apply(DysonSphere sphere)
    {
        sphere.energyReqCurrentTick = EnergyReqCurrentTick;
        sphere.energyGenCurrentTick = EnergyGenCurrentTick;
        sphere.energyGenOriginalCurrentTick = EnergyGenOriginalCurrentTick;
        sphere.swarm.energyGenCurrentTick = SwarmEnergyGenCurrentTick;
        sphere.grossRadius = GrossRadius;
    }
}
