#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory;

// Processes pasting settings (e.g. item to make in an assembler) onto buildings events
internal class PasteBuildingSettingUpdateProcessor : PacketProcessor<PasteBuildingSettingUpdate>
{
    protected override void ProcessPacket(PasteBuildingSettingUpdate packet, NebulaeConnection conn)
    {
        if (GameMain.galaxy.PlanetById(packet.PlanetId)?.factory == null)
        {
            return;
        }
        var backup = BuildingParameters.clipboard;
        BuildingParameters.clipboard = packet.GetBuildingSettings();
        using (Multiplayer.Session.Factories.IsIncomingRequest.On())
        {
            // skip audio and realtimetip update
            BuildingParameters.clipboard.PasteToFactoryObject(packet.ObjectId,
                GameMain.galaxy.PlanetById(packet.PlanetId).factory);
        }
        BuildingParameters.clipboard = backup;
    }
}
