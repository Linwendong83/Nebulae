#region

using NebulaeModel.DataStructures;

#endregion

namespace NebulaeModel.Packets.Session;

[NebulaeModel.Networking.PacketId(1779094798238025431UL)]
internal class PlayerJoining
{
    public PlayerJoining() { }

    public PlayerJoining(PlayerData playerData, ushort numPlayers)
    {
        PlayerData = playerData;
        NumPlayers = numPlayers;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public PlayerData PlayerData { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public ushort NumPlayers { get; set; }
}
