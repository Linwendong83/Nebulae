#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Planet;
using NebulaeModel.Packets.Players;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Planet;

// Processes events for mining vegetation or veins
internal class VegeMinedProcessor : PacketProcessor<VegeMinedPacket>
{
    protected override void ProcessPacket(VegeMinedPacket packet, NebulaeConnection conn)
    {
        var planetData = GameMain.galaxy.PlanetById(packet.PlanetId);
        var factory = planetData?.factory;
        if (factory is not { vegePool: not null })
        {
            return;
        }
        if (packet.VegeId <= 0 || packet.IsVein &&
            (factory.veinPool == null || packet.VegeId >= factory.veinPool.Length ||
             factory.veinPool[packet.VegeId].id != packet.VegeId) ||
            !packet.IsVein &&
            (packet.VegeId >= factory.vegePool.Length || factory.vegePool[packet.VegeId].id != packet.VegeId))
            return;
        ushort author = 0;
        if (IsHost)
        {
            var player = Players.Get(conn, EConnectionStatus.Connected);
            if (player == null || player.Data.LocalStarId != planetData.star.id) return;
            author = player.Id;
        }
        using (Multiplayer.Session.Planets.IsIncomingRequest.On())
        {
            Multiplayer.Session.Planets.TargetPlanet = packet.PlanetId;
            if (packet.Amount == 0)
            {
                if (packet.IsVein)
                {
                    var veinData = factory.GetVeinData(packet.VegeId);
                    var veinProto = LDB.veins.Select((int)veinData.type);

                    factory.RemoveVeinWithComponents(packet.VegeId);

                    if (veinProto != null && GameMain.localPlanet == planetData)
                    {
                        VFEffectEmitter.Emit(veinProto.MiningEffect, veinData.pos,
                            Maths.SphericalRotation(veinData.pos, 0f));
                        VFAudio.Create(veinProto.MiningAudio, null, veinData.pos, true);
                    }
                }
                else
                {
                    var vegeData = factory.GetVegeData(packet.VegeId);
                    var vegeProto = LDB.veges.Select(vegeData.protoId);
                    if (IsHost && packet.IsCollected)
                        Multiplayer.Session.Vegetation.GetRemote(author)?.AddVegeToPlayer(vegeData.protoId, 1);

                    factory.RemoveVegeWithComponents(packet.VegeId);

                    if (vegeProto != null && GameMain.localPlanet == planetData)
                    {
                        VFEffectEmitter.Emit(vegeProto.MiningEffect, vegeData.pos,
                            Maths.SphericalRotation(vegeData.pos, 0f));
                        VFAudio.Create(vegeProto.MiningAudio, null, vegeData.pos, true);
                    }
                }
            }
            else
            {
                // Taken from if (!isInfiniteResource) part of PlayerAction_Mine.GameTick()
                var veinData = factory.GetVeinData(packet.VegeId);
                var veinGroups = factory.veinGroups;
                var groupIndex = veinData.groupIndex;

                // must be a vein/oil patch (i think the game treats them same now as oil patches can run out too)
                factory.veinPool[packet.VegeId].amount = packet.Amount;
                veinGroups[groupIndex].amount -= 1L;
            }
            Multiplayer.Session.Planets.TargetPlanet = SessionSentinels.PLANET_NONE;
        }
        if (IsHost && packet.Amount == 0)
        {
            Multiplayer.Session.Server.SendPacketToStarExclude(packet, planetData.star.id, conn);
            if (packet.IsCollected)
                conn.SendPacket(new VegetableCollectionSnapshotPacket(
                    Multiplayer.Session.Vegetation.CaptureRemote(author), true));
        }
    }
}
