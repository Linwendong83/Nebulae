#region

using NebulaeModel.DataStructures;

#endregion

namespace NebulaeModel.Packets.Session;

[NebulaeModel.Networking.PacketId(3015417578455077658UL)]
internal class HandshakeResponse
{
    public HandshakeResponse() { }

    public HandshakeResponse(in GameDesc gameDesc, byte[] combatSettingsData, bool isNewPlayer, PlayerData localPlayerData, ushort numPlayers)
    {
        GalaxyAlgo = gameDesc.galaxyAlgo;
        GoalLevel = (int)gameDesc.goalLevel;
        GalaxySeed = gameDesc.galaxySeed;
        StarCount = gameDesc.starCount;
        StarNameLCID = gameDesc.starNameLCID;
        ResourceMultiplier = gameDesc.resourceMultiplier;
        IsPeaceMode = gameDesc.isPeaceMode;
        IsSandboxMode = gameDesc.isSandboxMode;
        SavedThemeIds = gameDesc.savedThemeIds;
        CombatSettingsData = combatSettingsData;
        IsNewPlayer = isNewPlayer;
        LocalPlayerData = localPlayerData;
        NumPlayers = numPlayers;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int GalaxyAlgo { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int GoalLevel { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int GalaxySeed { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int StarCount { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public float ResourceMultiplier { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public bool IsPeaceMode { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public bool IsSandboxMode { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public int[] SavedThemeIds { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public byte[] CombatSettingsData { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public bool IsNewPlayer { get; set; }
    [NebulaeModel.Networking.WireOrder(10)]
    public PlayerData LocalPlayerData { get; set; }
    [NebulaeModel.Networking.WireOrder(11)]
    public ushort NumPlayers { get; set; }

    [NebulaeModel.Networking.WireOrder(12)]
    public int StarNameLCID { get; set; }

}
