#region

using System;
using NebulaeModel.Networking;

#endregion

namespace NebulaeModel.Packets.GameStates;

[NebulaeModel.Networking.PacketId(7977842743183535764UL)]
internal class GameStateRequest
{
    [NebulaeModel.Networking.WireOrder(0)]
    public long SentTimestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
