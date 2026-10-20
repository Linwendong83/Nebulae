#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameHistory;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.GameHistory;
internal class GameHistoryNotificationProcessor : PacketProcessor<GameHistoryNotificationPacket>
{
    protected override void ProcessPacket(GameHistoryNotificationPacket packet, NebulaeConnection conn)
    {
        if (IsHost && packet.Event != GameHistoryEvent.OneKeyUnlock)
        {
            Multiplayer.Session.Network.SendPacketExclude(packet, conn);
        }

        using (Multiplayer.Session.History.IsIncomingRequest.On())
        {
            switch (packet.Event)
            {
                case GameHistoryEvent.ResumeQueue:
                    GameMain.history.ResumeTechQueue();
                    break;
                case GameHistoryEvent.PauseQueue:
                    GameMain.history.PauseTechQueue();
                    break;
                case GameHistoryEvent.OneKeyUnlock:
                    UIRoot.instance.uiGame.techTree.Do1KeyUnlock();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(packet), "Unknown GameHistoryNotificationPacket type: " + packet.Event);
            }
        }
    }
}
