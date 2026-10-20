using System;
using System.Collections.Generic;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Combat;
using NebulaeModel.Packets.GameStates;
using NebulaeModel.Packets.Routers;
using NebulaeModel.Networking.Serialization;

namespace NebulaeModel.Networking;

internal static class RealtimePacketBatch
{
    internal const int MaxPackets = 16;
    internal const int MaxBytes = 128 * 1024;
    private static readonly HashSet<ulong> kinds = new()
    {
        WireContract.PacketId(typeof(AuthorityWorldStatePacket)), WireContract.PacketId(typeof(AuthorityLifecyclePacket)),
        WireContract.PacketId(typeof(AuthorityScopeDigestPacket)), WireContract.PacketId(typeof(AuthorityPlayerCombatStatePacket)),
        WireContract.PacketId(typeof(BattleVisualPacket)), WireContract.PacketId(typeof(PlayerWeaponPacket)),
        WireContract.PacketId(typeof(GameStateUpdate))
    };
    internal static bool CanInclude(byte[] packet) => packet?.Length >= 8 && packet.Length < MaxBytes && kinds.Contains(BitConverter.ToUInt64(packet, 0));
    internal static byte[] Encode(byte[][] packets, NebulaeNetPacketProcessor writer) =>
        writer.Write(new StarBroadcastPacket(packets, 0)); // Host -> client: unwrap only, never fan out.
}
