#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Session;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Session;
internal class GlobalGameDataResponseProcessor : PacketProcessor<GlobalGameDataResponse>
{
    protected override void ProcessPacket(GlobalGameDataResponse packet, NebulaeConnection conn)
    {
        if (IsHost) return;
        Multiplayer.Session.JoinSync.ReceivedData(packet.BinaryData?.Length ?? 0);
        if (packet.DataType == GlobalGameDataResponse.EDataType.PlanetBaseline)
        {
            Multiplayer.Session.Planets.ImportFactoryPresence(packet.BinaryData);
            return;
        }
        if (packet.DataType == GlobalGameDataResponse.EDataType.Ready)
        {
            Multiplayer.Session.JoinSync.MarkWorldDataReceived();
        }

        // Store the binary data in GameStatesManager then later overwrite those system in GameData.NewGame
        Multiplayer.Session.State.ImportGlobalGameData(packet);
    }
}
