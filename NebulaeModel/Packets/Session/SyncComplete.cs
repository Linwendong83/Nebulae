#region

using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.Contracts;
using NebulaeModel.DataStructures;

#endregion

namespace NebulaeModel.Packets.Session;

[NebulaeModel.Networking.PacketId(5025840719046569768UL)]
internal class SyncComplete
{
    public SyncComplete()
    {
        AllPlayers = Array.Empty<PlayerData>();
        ClientCert = Array.Empty<byte>();
    }

    public SyncComplete(IEnumerable<IPlayerData> otherPlayers)
    {
        AllPlayers = otherPlayers.Select(data => (PlayerData)data).ToArray();
        ClientCert = Array.Empty<byte>();
    }

    public SyncComplete(byte[] clientCert)
    {
        AllPlayers = Array.Empty<PlayerData>();
        ClientCert = clientCert;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public PlayerData[] AllPlayers { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public byte[] ClientCert { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public long JoinGeneration { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public long JoinSequence { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public bool IsFinal { get; set; }
}
