namespace NebulaeModel.Packets.Routers;

/// <summary>
///     Client to host envelope that carries already-serialized packets and the planet they belong to.
/// </summary>
/// <remarks>
///     The planet-scoped sibling of <see cref="StarBroadcastPacket" />: a client hands the host a blob plus
///     a planet id and the host fans the blob out to the subscribers of that planet. Several blobs ride
///     one envelope so an action touching several packet types pays the framing once.
/// </remarks>
[NebulaeModel.Networking.PacketId(17388443317505028215UL)]
internal class PlanetBroadcastPacket
{
    /// <summary>Ceiling on the blobs one envelope may carry, so a peer cannot make the host loop.</summary>
    public const int MaxPacketObjects = 256;

    public PlanetBroadcastPacket() { }

    public PlanetBroadcastPacket(byte[] packetObject, int planetId)
    {
        PacketObjects = [packetObject];
        PlanetId = planetId;
    }

    public PlanetBroadcastPacket(byte[][] packetObjects, int planetId)
    {
        PacketObjects = packetObjects;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public byte[][] PacketObjects { get; set; }

    [NebulaeModel.Networking.WireOrder(1)]
    public int PlanetId { get; set; }
}
