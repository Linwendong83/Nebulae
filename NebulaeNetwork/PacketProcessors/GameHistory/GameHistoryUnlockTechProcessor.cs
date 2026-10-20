#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameHistory;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.GameHistory;
internal class GameHistoryUnlockTechProcessor : PacketProcessor<GameHistoryUnlockTechPacket>
{
    protected override void ProcessPacket(GameHistoryUnlockTechPacket packet, NebulaeConnection conn)
    {
        if (IsHost && !GameMain.data.gameDesc.isSandboxMode) return;
        if (!GameMain.history.techStates.TryGetValue(packet.TechId, out var existing)) return;
        if (existing.curLevel > packet.Level || existing.unlocked && existing.curLevel >= packet.Level) return;
        using (Multiplayer.Session.History.IsIncomingRequest.On())
        {
            // Let the default method give back the items
            if (GameMain.history.currentTech == packet.TechId)
            {
                if (IsHost) GameMain.mainPlayer.mecha.lab.ManageTakeback();
                else Multiplayer.Session.Research.TakeBackAfterPending();
            }

            // Update techState
            var techState = GameMain.history.techStates[packet.TechId];
            techState.curLevel = packet.Level;
            GameMain.history.techStates[packet.TechId] = techState;

            GameMain.history.UnlockTechUnlimited(packet.TechId, false);
            if (GameMain.history.currentTech == packet.TechId) GameMain.history.DequeueTech();
        }
    }
}
