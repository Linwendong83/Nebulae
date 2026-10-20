#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameStates;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.GameStates;
// ReSharper disable once UnusedType.Global
internal class GameStateUpdateProcessor : PacketProcessor<GameStateUpdate>
{
    protected override void ProcessPacket(GameStateUpdate packet, NebulaeConnection conn)
    {
        Multiplayer.Session.State.ProcessGameStateUpdatePacket(packet.SentTime, packet.GameTick, packet.UnitsPerSecond, packet.SimSpeed);
    }
}
