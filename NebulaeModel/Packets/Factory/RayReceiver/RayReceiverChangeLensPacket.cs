namespace NebulaeModel.Packets.Factory.RayReceiver;

[NebulaeModel.Networking.PacketId(5394518212512614700UL)]
internal class RayReceiverChangeLensPacket
{
    public RayReceiverChangeLensPacket() { }

    public RayReceiverChangeLensPacket(int generatorId, PowerGeneratorComponent generator, int planetId)
    {
        GeneratorId = generatorId;
        CatalystId = generator.catalystId;
        CurrentCatalystId = generator.curCatalystId;
        CatalystCount = generator.catalystCount;
        CatalystInc = generator.catalystInc;
        CatalystMask = generator.catalystMask;
        CatalystIncLevel = generator.catalystIncLevel;
        LensCount = generator.catalystPoint;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int GeneratorId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int LensCount { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int CatalystId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int CurrentCatalystId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public short CatalystCount { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public short CatalystInc { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public short CatalystMask { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public byte CatalystIncLevel { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public int PlanetId { get; set; }
}
