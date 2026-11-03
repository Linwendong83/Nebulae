using System;
using System.Collections.Generic;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Combat;
using NebulaeModel.Packets.Factory;
using NebulaeModel.Packets.GameStates;
using NebulaeModel.Packets.Routers;
using NebulaeModel.Packets.Trash;
using NebulaeModel.Networking.Serialization;

namespace NebulaeModel.Networking;

internal static class RealtimePacketBatch
{
    // A dismantled container can mint hundreds of trash objects and one packet per object. Batching
    // up to 64 frames keeps that burst from becoming a socket write per object; the byte ceiling
    // still bounds the envelope and the queue preserves child order.
    internal const int MaxPackets = 64;
    internal const int MaxBytes = 128 * 1024;
    private static readonly HashSet<ulong> kinds = new()
    {
        WireContract.PacketId(typeof(AuthorityWorldStatePacket)), WireContract.PacketId(typeof(AuthorityLifecyclePacket)),
        WireContract.PacketId(typeof(AuthorityScopeDigestPacket)), WireContract.PacketId(typeof(AuthorityPlayerCombatStatePacket)),
        WireContract.PacketId(typeof(BattleVisualPacket)), WireContract.PacketId(typeof(PlayerWeaponPacket)),
        WireContract.PacketId(typeof(GameStateUpdate)), WireContract.PacketId(typeof(PrebuildInstanceBatchPacket)),
        WireContract.PacketId(typeof(BuildEntityRequest)), WireContract.PacketId(typeof(DestructEntityRequest)),
        WireContract.PacketId(typeof(TrashSystemNewPlanetTrashPacket)), WireContract.PacketId(typeof(TrashSystemNewPlayerTrashPacket))
    };

    // Client -> host combining is limited to the burst operations that motivated it. The client
    // envelope is applied by the host and not fanned out, so it must never carry a packet whose
    // processor expects the host to relay it separately.
    private static readonly HashSet<ulong> clientKinds = new()
    {
        WireContract.PacketId(typeof(BuildEntityRequest)), WireContract.PacketId(typeof(DestructEntityRequest)),
        WireContract.PacketId(typeof(TrashSystemNewPlanetTrashPacket)), WireContract.PacketId(typeof(TrashSystemNewPlayerTrashPacket))
    };

    /// <summary>Star id that no player can hold, so a transport batch is never fanned out.</summary>
    internal const int UnroutableStarId = int.MinValue;

    internal static bool CanInclude(byte[] packet, bool hostSide = true)
    {
        if (packet == null || packet.Length < 8 || packet.Length >= MaxBytes) return false;
        var id = BitConverter.ToUInt64(packet, 0);
        return kinds.Contains(id) && (hostSide || clientKinds.Contains(id));
    }
    internal static byte[] Encode(byte[][] packets, NebulaeNetPacketProcessor writer) =>
        writer.Write(new StarBroadcastPacket(packets, UnroutableStarId)); // Unwrap only, never fan out.
}
