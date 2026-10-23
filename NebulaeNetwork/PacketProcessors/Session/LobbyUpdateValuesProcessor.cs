#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Session;

#endregion

namespace NebulaeNetwork.PacketProcessors.Session;
internal class LobbyUpdateValuesProcessor : PacketProcessor<LobbyUpdateValues>
{
    protected override void ProcessPacket(LobbyUpdateValues packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            return;
        }

        var gameDesc = new GameDesc();
        gameDesc.SetForNewGame(packet.GalaxyAlgo, packet.GalaxySeed, packet.StarCount, 1, packet.ResourceMultiplier);
        gameDesc.starNameLCID = packet.StarNameLCID;
        gameDesc.isSandboxMode = packet.IsSandboxMode;
        gameDesc.isPeaceMode = packet.IsPeaceMode;
        gameDesc.goalLevel = (EGoalLevel)packet.GoalLevel;
        if (!packet.IsPeaceMode)
        {
            gameDesc.combatSettings.aggressiveness = packet.CombatAggressiveness;
            gameDesc.combatSettings.initialLevel = packet.CombatInitialLevel;
            gameDesc.combatSettings.initialGrowth = packet.CombatInitialGrowth;
            gameDesc.combatSettings.initialColonize = packet.CombatInitialColonize;
            gameDesc.combatSettings.maxDensity = packet.CombatMaxDensity;
            gameDesc.combatSettings.growthSpeedFactor = packet.CombatGrowthSpeedFactor;
            gameDesc.combatSettings.powerThreatFactor = packet.CombatPowerThreatFactor;
            gameDesc.combatSettings.battleThreatFactor = packet.CombatBattleThreatFactor;
            gameDesc.combatSettings.battleExpFactor = packet.CombatBattleExpFactor;
        }

        NebulaeModel.Utils.NativeGameAccess.SetGalaxyDescription(UIRoot.instance.galaxySelect, gameDesc);
        UIRoot.instance.galaxySelect.uiCombat.gameDesc = gameDesc;
        UIRoot.instance.galaxySelect.GoalSetting.gameDesc = gameDesc;
        UIRoot.instance.galaxySelect.SetStarmapGalaxy();
        UIRoot.instance.galaxySelect.sandboxToggle.isOn = packet.IsSandboxMode;
    }
}
