namespace NebulaeModel.Packets.Session;

[NebulaeModel.Networking.PacketId(12262124202057944567UL)]
internal class LobbyResponse
{
    public LobbyResponse() { }

    public LobbyResponse(in GameDesc gameDesc, byte[] combatSettingsData, ushort numPlayers)
    {
        GalaxyAlgo = gameDesc.galaxyAlgo;
        GoalLevel = (int)gameDesc.goalLevel;
        GalaxySeed = gameDesc.galaxySeed;
        StarCount = gameDesc.starCount;
        StarNameLCID = gameDesc.starNameLCID;
        ResourceMultiplier = gameDesc.resourceMultiplier;
        IsPeaceMode = gameDesc.isPeaceMode;
        IsSandboxMode = gameDesc.isSandboxMode;
        CombatSettingsData = combatSettingsData;
        SavedThemeIds = gameDesc.savedThemeIds;
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
    public ushort NumPlayers { get; set; }

    [NebulaeModel.Networking.WireOrder(10)]
    public int StarNameLCID { get; set; }
}
