namespace NebulaeModel.Packets.Factory.RayReceiver;

[NebulaeModel.Networking.PacketId(7827029125195183559UL)]
internal class RayReceiverChangeModePacket
{
    public RayReceiverChangeModePacket() { }

    public RayReceiverChangeModePacket(int generatorId, RayReceiverMode mode, int planetId)
    {
        GeneratorId = generatorId;
        Mode = mode;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int GeneratorId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public RayReceiverMode Mode { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int PlanetId { get; set; }
}

internal enum RayReceiverMode
{
    Electricity = 0,
    Photon = 1
}
