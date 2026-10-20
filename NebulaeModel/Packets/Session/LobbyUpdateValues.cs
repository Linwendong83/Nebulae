namespace NebulaeModel.Packets.Session;

[NebulaeModel.Networking.PacketId(2378329177243543407UL)]
internal class LobbyUpdateValues
{
    public LobbyUpdateValues() { }

    public LobbyUpdateValues(in GameDesc gameDesc)
    {
        GalaxyAlgo = gameDesc.galaxyAlgo;
        GoalLevel = (int)gameDesc.goalLevel;
        GalaxySeed = gameDesc.galaxySeed;
        StarCount = gameDesc.starCount;
        StarNameLCID = gameDesc.starNameLCID;
        ResourceMultiplier = gameDesc.resourceMultiplier;
        IsSandboxMode = gameDesc.isSandboxMode;
        IsPeaceMode = gameDesc.isPeaceMode;
        var combatSettings = gameDesc.combatSettings;
        CombatAggressiveness = combatSettings.aggressiveness;
        CombatInitialLevel = combatSettings.initialLevel;
        CombatInitialGrowth = combatSettings.initialGrowth;
        CombatInitialColonize = combatSettings.initialColonize;
        CombatMaxDensity = combatSettings.maxDensity;
        CombatGrowthSpeedFactor = combatSettings.growthSpeedFactor;
        CombatPowerThreatFactor = combatSettings.powerThreatFactor;
        CombatBattleThreatFactor = combatSettings.battleThreatFactor;
        CombatBattleExpFactor = combatSettings.battleExpFactor;
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
    public bool IsSandboxMode { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public bool IsPeaceMode { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public float CombatAggressiveness { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public float CombatInitialLevel { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public float CombatInitialGrowth { get; set; }
    [NebulaeModel.Networking.WireOrder(10)]
    public float CombatInitialColonize { get; set; }
    [NebulaeModel.Networking.WireOrder(11)]
    public float CombatMaxDensity { get; set; }
    [NebulaeModel.Networking.WireOrder(12)]
    public float CombatGrowthSpeedFactor { get; set; }
    [NebulaeModel.Networking.WireOrder(13)]
    public float CombatPowerThreatFactor { get; set; }
    [NebulaeModel.Networking.WireOrder(14)]
    public float CombatBattleThreatFactor { get; set; }
    [NebulaeModel.Networking.WireOrder(15)]
    public float CombatBattleExpFactor { get; set; }

    [NebulaeModel.Networking.WireOrder(16)]
    public int StarNameLCID { get; set; }
}
