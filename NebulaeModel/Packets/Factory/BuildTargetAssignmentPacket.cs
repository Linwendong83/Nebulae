using NebulaeModel.DataStructures;

namespace NebulaeModel.Packets.Factory;

[NebulaeModel.Networking.PacketId(3968821821035000974UL)]
internal class BuildTargetAssignmentPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PrebuildId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public long Generation { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public BuildOwnerKind OwnerKind { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int OwnerId { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public bool Launched { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public bool Removed { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public bool Canceled { get; set; }

    public BuildTargetAssignmentPacket() { }

    public BuildTargetAssignmentPacket(BuildTargetClaim claim, bool removed = false, bool canceled = false)
    {
        PlanetId = claim.PlanetId;
        PrebuildId = claim.PrebuildId;
        Generation = claim.Generation;
        OwnerKind = claim.OwnerKind;
        OwnerId = claim.OwnerId;
        Launched = claim.Launched;
        Removed = removed;
        Canceled = canceled;
    }

    public BuildTargetClaim ToClaim() =>
        new(PlanetId, PrebuildId, Generation, OwnerKind, OwnerId, Launched);
}

[NebulaeModel.Networking.PacketId(4855613588810004546UL)]
internal class BuildTargetAssignmentReplyPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PrebuildId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public long Generation { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public bool Accepted { get; set; }

    public BuildTargetAssignmentReplyPacket() { }

    public BuildTargetAssignmentReplyPacket(int planetId, int prebuildId, long generation, bool accepted)
    {
        PlanetId = planetId;
        PrebuildId = prebuildId;
        Generation = generation;
        Accepted = accepted;
    }
}

[NebulaeModel.Networking.PacketId(18347838477915881878UL)]
internal class BuildTargetReadyPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PrebuildId { get; set; }
}

[NebulaeModel.Networking.PacketId(5300928144242603124UL)]
internal class BuildTargetBaseReleaseAckPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PrebuildId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public long Generation { get; set; }

    public BuildTargetBaseReleaseAckPacket() { }

    public BuildTargetBaseReleaseAckPacket(int planetId, int prebuildId, long generation)
    {
        PlanetId = planetId;
        PrebuildId = prebuildId;
        Generation = generation;
    }
}

[NebulaeModel.Networking.PacketId(14065765317234901511UL)]
internal class BuildDroneLaunchPacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int TargetObjectId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int Next1ObjectId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int Next2ObjectId { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int Next3ObjectId { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public long TargetGeneration { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public long Next1Generation { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public long Next2Generation { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public long Next3Generation { get; set; }
    [NebulaeModel.Networking.WireOrder(10)]
    public int DronePriority { get; set; }
}
