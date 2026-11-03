#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Players;

internal class PlayerMapPositionProcessor : PacketProcessor<PlayerMapPosition>
{
    protected override void ProcessPacket(PlayerMapPosition packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            return;
        }

        // A player in another system is only observable as a dot on the star map, so their position is
        // written straight through instead of through the interpolation buffer: there is nothing to
        // interpolate at one update a second.
        if (!Finite(packet.UPosition.x) || !Finite(packet.UPosition.y) || !Finite(packet.UPosition.z) ||
            Math.Abs(packet.UPosition.x) > 1e12 || Math.Abs(packet.UPosition.y) > 1e12 ||
            Math.Abs(packet.UPosition.z) > 1e12) return;
        if (GameMain.galaxy == null) return;
        if (packet.LocalPlanetId > 0 && GameMain.galaxy.PlanetById(packet.LocalPlanetId) == null) return;
        using (Multiplayer.Session.World.GetRemotePlayersModels(out var remotePlayersModels))
        {
            if (!remotePlayersModels.TryGetValue(packet.PlayerId, out var model))
            {
                return;
            }
            model.Movement.UpdateMapPosition(packet);
        }
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
