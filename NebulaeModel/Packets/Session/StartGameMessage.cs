#region

using NebulaeModel.DataStructures;

#endregion

namespace NebulaeModel.Packets.Session;

[NebulaeModel.Networking.PacketId(16741000459130034613UL)]
internal class StartGameMessage
{
    public StartGameMessage() { }

    public StartGameMessage(bool isAllowedToStart, PlayerData localPlayerData, bool isNewPlayer = false)
    {
        IsAllowedToStart = isAllowedToStart;
        LocalPlayerData = localPlayerData;
        IsNewPlayer = isNewPlayer;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public bool IsAllowedToStart { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public PlayerData LocalPlayerData { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public bool IsNewPlayer { get; set; }
}
