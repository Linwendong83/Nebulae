#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Combat;

#endregion

namespace NebulaeNetwork.PacketProcessors.Combat;
internal class CombatAggressivenessProcessor : PacketProcessor<CombatAggressivenessUpdatePacket>
{
    protected override void ProcessPacket(CombatAggressivenessUpdatePacket packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            return; // Only a committed metadata transaction may change the world's difficulty.
        }

        var history = GameMain.history;
        var oldAggressiveLevel = history.combatSettings.aggressiveLevel;
        history.combatSettings.aggressiveness = packet.Aggressiveness;

        var currentPropertyMultiplier = history.currentPropertyMultiplier;
        if (currentPropertyMultiplier < history.minimalPropertyMultiplier)
        {
            history.minimalPropertyMultiplier = currentPropertyMultiplier;
        }
        var difficulty = history.combatSettings.difficulty;
        if (difficulty < history.minimalDifficulty)
        {
            history.minimalDifficulty = difficulty;
        }
        GameMain.data.spaceSector.OnDFAggressivenessChanged(oldAggressiveLevel, history.combatSettings.aggressiveLevel);

    }
}
