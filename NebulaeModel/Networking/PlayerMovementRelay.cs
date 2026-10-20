using NebulaeModel.Contracts;
using NebulaeModel.Packets.Players;

namespace NebulaeModel.Networking;

/// <summary>Routes one player's presentation to nearby peers and a slower map update to everyone else.</summary>
internal sealed class PlayerMovementRelay
{
    private const float MapPositionInterval = 1f;
    private bool hasMapPosition;
    private float lastMapPositionAt;
    private int lastStarId, lastPlanetId;

    public void Send(INetworkProvider network, PlayerMovementDisplay display, int starId, float now,
        INebulaeConnection exclude = null)
    {
        network.SendPacketToStarExclude(display, starId, exclude);
        // A transition must leave the old star with the new map position immediately. A clock reset
        // must not suppress updates until the old timestamp is reached again.
        if (hasMapPosition && starId == lastStarId && display.LocalPlanetId == lastPlanetId &&
            now >= lastMapPositionAt && now - lastMapPositionAt < MapPositionInterval) return;

        hasMapPosition = true;
        lastMapPositionAt = now;
        lastStarId = starId;
        lastPlanetId = display.LocalPlanetId;
        network.SendToMatching(new PlayerMapPosition(display.PlayerId, display.LocalPlanetId, display.UPosition),
            p => p.Data.LocalStarId != starId && !p.Connection.Equals(exclude));
    }
}
