#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Tank;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Tank;
internal class SprayerStorageUpdateProcessor : PacketProcessor<SprayerStorageUpdatePacket>
{
    protected override void ProcessPacket(SprayerStorageUpdatePacket packet, NebulaeConnection conn)
    {
        var pool = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.cargoTraffic.spraycoaterPool;
        if (pool == null || packet.SprayerIndex < 0 || packet.SprayerIndex >= pool.Length || pool[packet.SprayerIndex].id == -1)
        {
            return;
        }
        pool[packet.SprayerIndex].incItemId = packet.IncItemId;
        pool[packet.SprayerIndex].incAbility = packet.IncAbility;
        pool[packet.SprayerIndex].incSprayTimes = packet.IncSprayTimes;
        pool[packet.SprayerIndex].incCount = packet.IncCount;
        pool[packet.SprayerIndex].extraIncCount = packet.ExtraIncCount;
    }
}
