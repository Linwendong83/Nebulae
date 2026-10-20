#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.PowerGenerator;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.PowerGenerator;
internal class PowerGeneratorFuelUpdateProcessor : PacketProcessor<PowerGeneratorFuelUpdatePacket>
{
    protected override void ProcessPacket(PowerGeneratorFuelUpdatePacket packet, NebulaeConnection conn)
    {
        var pool = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.powerSystem?.genPool;
        if (pool == null || packet.PowerGeneratorIndex == -1 || packet.PowerGeneratorIndex >= pool.Length ||
            pool[packet.PowerGeneratorIndex].id == -1)
        {
            return;
        }
        if (pool[packet.PowerGeneratorIndex].fuelId != packet.FuelId)
        {
            pool[packet.PowerGeneratorIndex].SetNewFuel(packet.FuelId, packet.FuelAmount, packet.FuelInc);
        }
        else
        {
            pool[packet.PowerGeneratorIndex].fuelCount = packet.FuelAmount;
        }
    }
}
