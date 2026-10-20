using System;
using NebulaeModel.Logger;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Combat;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Combat;

internal sealed class PlayerWeaponProcessor : PacketProcessor<PlayerWeaponPacket>
{
    protected override void ProcessPacket(PlayerWeaponPacket packet, NebulaeConnection conn)
    {
        if (IsHost) return;
        try { Multiplayer.Session.PlayerWeapons.Receive(packet); }
        catch (Exception e) { Log.Warn("Invalid host player weapon frame: " + e.Message); }
    }
}
