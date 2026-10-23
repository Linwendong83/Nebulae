#region

using NebulaeModel.DataStructures;

#endregion

namespace NebulaeModel.Packets.Session;

[NebulaeModel.Networking.PacketId(16741000459130034613UL)]
internal class StartGameMessage
{
    public StartGameMessage() : this(false, null) { }

    public StartGameMessage(bool isAllowedToStart, PlayerData localPlayerData, bool isNewPlayer = false)
    {
        IsAllowedToStart = isAllowedToStart;
        // Nested PlayerData has no null marker on the wire. Requests and busy responses
        // still need a complete placeholder so the receiver can read the trailing bool.
        LocalPlayerData = localPlayerData ?? new PlayerData(0, 0);
        IsNewPlayer = isNewPlayer;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public bool IsAllowedToStart { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public PlayerData LocalPlayerData { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public bool IsNewPlayer { get; set; }
}
