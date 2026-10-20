namespace NebulaeModel.Packets.Session
{
    [NebulaeModel.Networking.PacketId(16172185058990731667UL)]
    internal class LobbyUpdateCombatValues
    {
        public LobbyUpdateCombatValues() { }

        public LobbyUpdateCombatValues(CombatSettings combatSettings)
        {
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
        public float CombatAggressiveness { get; set; }
        [NebulaeModel.Networking.WireOrder(1)]
        public float CombatInitialLevel { get; set; }
        [NebulaeModel.Networking.WireOrder(2)]
        public float CombatInitialGrowth { get; set; }
        [NebulaeModel.Networking.WireOrder(3)]
        public float CombatInitialColonize { get; set; }
        [NebulaeModel.Networking.WireOrder(4)]
        public float CombatMaxDensity { get; set; }
        [NebulaeModel.Networking.WireOrder(5)]
        public float CombatGrowthSpeedFactor { get; set; }
        [NebulaeModel.Networking.WireOrder(6)]
        public float CombatPowerThreatFactor { get; set; }
        [NebulaeModel.Networking.WireOrder(7)]
        public float CombatBattleThreatFactor { get; set; }
        [NebulaeModel.Networking.WireOrder(8)]
        public float CombatBattleExpFactor { get; set; }
    }
}
