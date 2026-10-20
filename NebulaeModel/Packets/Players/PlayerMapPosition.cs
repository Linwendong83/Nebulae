#region

using NebulaeModel.DataStructures;

#endregion

namespace NebulaeModel.Packets.Players;

/// <summary>
///     Where a player in another star system is, for the star map.
/// </summary>
/// <remarks>
///     A player the receiver cannot see still needs a position on the star map, but nothing about their
///     animation or their planet-relative pose is observable from another system. This carries only what
///     the map reads, and the host sends it far less often than it relays the full pose.
/// </remarks>
[NebulaeModel.Networking.PacketId(1120938475610293848UL)]
internal class PlayerMapPosition
{
    public PlayerMapPosition() { }

    public PlayerMapPosition(ushort playerId, int localPlanetId, Double3 uPosition)
    {
        PlayerId = playerId;
        LocalPlanetId = localPlanetId;
        UPosition = uPosition;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }

    [NebulaeModel.Networking.WireOrder(1)]
    public int LocalPlanetId { get; set; }

    [NebulaeModel.Networking.WireOrder(2)]
    public Double3 UPosition { get; set; }
}
